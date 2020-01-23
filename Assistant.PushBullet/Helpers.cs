using Assistant.PushBullet.Logging;
using System;
using System.Net.NetworkInformation;
using System.Threading;

namespace Assistant.PushBullet {
	public static class Helpers {
		public static bool CheckForInternetConnection() {
			try {
				Ping myPing = new Ping();
				string host = "8.8.8.8";
				byte[] buffer = new byte[32];
				int timeout = 1000;
				PingOptions pingOptions = new PingOptions();
				PingReply reply = myPing.Send(host, timeout, buffer, pingOptions);
				return reply != null && reply.Status == IPStatus.Success;
			}
			catch (Exception e) {
				EventLogger.LogException(e);
				return false;
			}
		}

		public static Timer? ScheduleTask(Action action, TimeSpan delay) {
			if (action == null) {
				EventLogger.LogWarning("Action is null! " + nameof(action));
				return null;
			}

			Timer? TaskSchedulerTimer = null;

			TaskSchedulerTimer = new Timer(e => {
				InBackgroundThread(action, delay.TotalMilliseconds.ToString());

				TaskSchedulerTimer?.Dispose();
			}, null, delay, delay);

			return TaskSchedulerTimer;
		}

		public static Thread? InBackgroundThread(Action action, string threadName, bool longRunning = false) {
			if (action == null) {
				EventLogger.LogWarning("Action is null! " + nameof(action));
				return null;
			}

			ThreadStart threadStart = new ThreadStart(action);
			Thread BackgroundThread = new Thread(threadStart);

			if (longRunning) {
				BackgroundThread.IsBackground = true;
			}

			BackgroundThread.Name = threadName;
			BackgroundThread.Priority = ThreadPriority.Normal;
			BackgroundThread.Start();
			return BackgroundThread;
		}
	}
}
