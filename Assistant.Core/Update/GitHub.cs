using Assistant.Extensions;
using Assistant.Logging;
using Assistant.Logging.Interfaces;
using Newtonsoft.Json;
using RestSharp;
using System;
using static Assistant.Logging.Enums;

namespace Assistant.Core.Update {
	internal class GitHub {

		[JsonProperty("url")]
		public string ReleaseUrl { get; set; } = string.Empty;

		[JsonProperty("tag_name")]
		public string ReleaseTagName { get; set; } = string.Empty;

		[JsonProperty("name")]
		public string ReleaseFileName { get; set; } = string.Empty;

		[JsonProperty("published_at")]
		public DateTime PublishedAt { get; set; }

		[JsonProperty("assets")]
		public Asset[]? Assets { get; set; }

		public class Asset {
			[JsonProperty("id")]
			public int AssetId { get; set; }

			[JsonProperty("browser_download_url")]
			public string AssetDownloadUrl { get; set; } = string.Empty;
		}

		private readonly ILogger Logger = new Logger("GITHUB");

		public GitHub FetchLatestAssest() {
			string? json = Helpers.GetUrlToString(Constants.GitHubReleaseURL, Method.GET);

			if (string.IsNullOrEmpty(json)) {
				Logger.Log("Could not fetch the latest patch release. Try again later!", LogLevels.Warn);
				return new GitHub();
			}

			return JsonConvert.DeserializeObject<GitHub>(json);
		}
	}
}
