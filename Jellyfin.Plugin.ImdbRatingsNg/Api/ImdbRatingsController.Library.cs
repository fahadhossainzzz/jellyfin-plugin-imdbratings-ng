using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatingsNg.Api
{
    /// <summary>
    /// NoBars API: library extras: where a file came from, subtitle checks, deleting a version.
    /// </summary>
    public partial class ImdbRatingsController
    {
        /// <summary>
        /// Gets the original download's release name of a film or episode file (e.g. "Movie.2019.1080p.BluRay.x264-GRP"),
        /// from origins.json in this plugin's data folder (written by the M2 server's importer). The TV's subtitle search
        /// uses it to mark subtitles made for the same release.
        /// </summary>
        /// <param name="itemId">The film or episode.</param>
        /// <response code="200">{"name": "release name"}.</response>
        /// <response code="404">Unknown item, or no name recorded for its file.</response>
        /// <returns>The JSON.</returns>
        [HttpGet("Origin/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetOrigin([FromRoute] Guid itemId)
        {
            var folder = Plugin.Instance?.DataFolderPath;
            var item = _libraryManager.GetItemById(itemId);
            if (string.IsNullOrEmpty(folder) || item is null || string.IsNullOrEmpty(item.Path))
            {
                return NotFound();
            }

            var file = Path.Combine(folder, "origins.json");
            if (!System.IO.File.Exists(file))
            {
                return NotFound();
            }

            try
            {
                var map = JsonNode.Parse(System.IO.File.ReadAllText(file))?.AsObject();
                var stem = Path.Combine(Path.GetDirectoryName(item.Path) ?? string.Empty, Path.GetFileNameWithoutExtension(item.Path));
                var name = map?[stem]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    return NotFound();
                }

                return Content(new JsonObject { ["name"] = name }.ToJsonString(), MediaTypeNames.Application.Json);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "origins.json unreadable");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "origins.json unreadable");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "origins.json has an unexpected shape");
            }

            return NotFound();
        }

        /// <summary>
        /// Asks the M2 server to check, and if needed fix, the timing of the subtitle just downloaded for this item
        /// (subtitle-checks/{id}.json in this plugin's data folder; the importer picks it up within 2 minutes).
        /// </summary>
        /// <param name="itemId">The film or episode.</param>
        /// <response code="200">{"status": "waiting"}.</response>
        /// <response code="404">Unknown item.</response>
        /// <returns>The JSON.</returns>
        [HttpPost("CheckSubtitle/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult RequestSubtitleCheck([FromRoute] Guid itemId)
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder) || _libraryManager.GetItemById(itemId) is null)
            {
                return NotFound();
            }

            var dir = Path.Combine(folder, "subtitle-checks");
            var id = itemId.ToString("N", CultureInfo.InvariantCulture);
            Directory.CreateDirectory(dir);
            var request = new JsonObject
            {
                ["id"] = id,
                ["requested"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            }.ToJsonString();
            var path = Path.Combine(dir, id + ".json");
            System.IO.File.WriteAllText(path + ".tmp", request);
            System.IO.File.Move(path + ".tmp", path, true);
            System.IO.File.Delete(Path.Combine(dir, id + ".result.json"));
            return Content(new JsonObject { ["status"] = "waiting" }.ToJsonString(), MediaTypeNames.Application.Json);
        }

        /// <summary>
        /// Gets how the subtitle timing check for this item went: "waiting", "checking", "ok" (in sync), "fixed"
        /// (re-timed to the film's speech) or "flagged" (doesn't fit this file), with a short message.
        /// </summary>
        /// <param name="itemId">The film or episode.</param>
        /// <response code="200">The status as JSON.</response>
        /// <response code="404">No check asked for.</response>
        /// <returns>The JSON.</returns>
        [HttpGet("CheckSubtitle/{itemId}")]
        [Authorize]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetSubtitleCheck([FromRoute] Guid itemId)
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(folder))
            {
                return NotFound();
            }

            var dir = Path.Combine(folder, "subtitle-checks");
            var id = itemId.ToString("N", CultureInfo.InvariantCulture);
            if (System.IO.File.Exists(Path.Combine(dir, id + ".json")))
            {
                return Content(new JsonObject { ["status"] = "waiting" }.ToJsonString(), MediaTypeNames.Application.Json);
            }

            var result = Path.Combine(dir, id + ".result.json");
            return System.IO.File.Exists(result) ? PhysicalFile(result, MediaTypeNames.Application.Json) : NotFound();
        }

        /// <summary>
        /// Deletes one version (one file) of a film or episode that has two or more, leaving the others and every
        /// subtitle file in the folder alone (they may belong to another version). Refuses when it is the only version,
        /// when the file is outside the libraries' folders, or while the title is playing. Then starts a library scan,
        /// so the title shows the remaining version(s).
        /// </summary>
        /// <param name="itemId">The film or episode.</param>
        /// <param name="sourceId">The version (media source id) to delete.</param>
        /// <response code="200">{"ok": true|false, "message": "..."}.</response>
        /// <response code="404">Unknown item.</response>
        /// <returns>The JSON.</returns>
        [HttpPost("DeleteVersion/{itemId}/{sourceId}")]
        [Authorize(Policy = "RequiresElevation")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult DeleteVersion([FromRoute] Guid itemId, [FromRoute] string sourceId)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null || !Guid.TryParse(sourceId, out var sourceGuid))
            {
                return NotFound();
            }

            var sources = _mediaSourceManager.GetStaticMediaSources(item, false);
            if (sources.Count < 2)
            {
                return Answer(false, "This title has only one version; use Delete instead");
            }

            MediaBrowser.Model.Dto.MediaSourceInfo? source = null;
            foreach (var s in sources)
            {
                if (Guid.TryParse(s.Id, out var g) && g == sourceGuid)
                {
                    source = s;
                }
            }

            if (source is null || string.IsNullOrEmpty(source.Path) || !System.IO.File.Exists(source.Path))
            {
                return Answer(false, "That version's file wasn't found");
            }

            var path = Path.GetFullPath(source.Path);
            bool inLibrary = false;
            foreach (var folder in _libraryManager.GetVirtualFolders())
            {
                foreach (var location in folder.Locations ?? Array.Empty<string>())
                {
                    var root = Path.GetFullPath(location).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (path.StartsWith(root, StringComparison.Ordinal))
                    {
                        inLibrary = true;
                    }
                }
            }

            if (!inLibrary)
            {
                return Answer(false, "That file is outside the library folders");
            }

            foreach (var session in _sessionManager.Sessions)
            {
                var playing = session.NowPlayingItem;
                if (playing is null)
                {
                    continue;
                }

                if (playing.Id == itemId || playing.Id == sourceGuid
                    || (Guid.TryParse(session.PlayState?.MediaSourceId, out var playingSource) && playingSource == sourceGuid))
                {
                    return Answer(false, "This title is playing; stop it first");
                }
            }

            try
            {
                System.IO.File.Delete(path);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Deleting version {Path} failed", path);
                return Answer(false, "Couldn't delete the file: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Deleting version {Path} failed", path);
                return Answer(false, "Not allowed to delete the file");
            }

            _logger.LogInformation("Deleted one version of {Name}: {Path}", item.Name, path);
            _libraryManager.QueueLibraryScan();
            return Answer(true, "Version deleted (" + Path.GetFileName(path) + "). The library updates in a minute or two.");
        }
    }
}
