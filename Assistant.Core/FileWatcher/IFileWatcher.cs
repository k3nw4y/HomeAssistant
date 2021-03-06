using Assistant.Logging.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;

namespace Assistant.Core.FileWatcher {
	public interface IFileWatcher {
		ILogger Logger { get; set; }

		bool IsOnline { get; set; }

		string? WatcherFilter { get; set; }

		Dictionary<string, Action> WatcherFileEvents { get; set; }

		string? WatcherDirectory { get; set; }

		List<string> IgnoreList { get; set; }

		FileSystemWatcher? Watcher { get; set; }

		DateTime LastRead { get; set; }

		void InitWatcher(string? dir, Dictionary<string, Action> watcherFileEvents, List<string> ignoreList, string? filter = "*.json", bool includeSubs = false);

		void StopWatcher();
	}
}
