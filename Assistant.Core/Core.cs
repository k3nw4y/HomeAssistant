using Assistant.Core.FileWatcher;
using Assistant.Core.Update;
using Assistant.Extensions;
using Assistant.Gpio;
using Assistant.Logging;
using Assistant.Logging.Interfaces;
using Assistant.Modules;
using Assistant.Pushbullet;
using Assistant.Server.CoreServer;
using Assistant.Sound.Speech;
using Assistant.Weather;
using CommandLine;
using Google.Protobuf.WellKnownTypes;
using RestSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unosquare.RaspberryIO;
using static Assistant.Gpio.PiController;
using static Assistant.Logging.Enums;
using static Assistant.Modules.ModuleInitializer;

namespace Assistant.Core {
	public class Options {

		[Option('d', "debug", Required = false, HelpText = "Displays all Trace level messages to console. (for debugging)")]
		public bool Debug { get; set; }

		[Option('s', "safe", Required = false, HelpText = "Enables safe mode so that only preconfigured pins can be modified.")]
		public bool Safe { get; set; }

		[Option('f', "firstchance", Required = false, HelpText = "Enables logging of first chance exceptions to console.")]
		public bool EnableFirstChance { get; set; }

		[Option('t', "tts", Required = false, HelpText = "Enable text to speech system for assistant.")]
		public bool TextToSpeech { get; set; }

		[Option("df", Required = false, HelpText = "Disable first chance exception logging when debug mode is enabled.")]
		public bool DisableFirstChance { get; set; }
	}

	public class Core {
		public static ILogger Logger { get; set; } = new Logger("ASSISTANT");
		private static List<NLog.CoreLogger> AssistantLoggers = new List<NLog.CoreLogger>();
		public static DateTime StartupTime;
		private static Timer? RefreshConsoleTitleTimer;
		private static Gpio.Gpio? GpioCore;

		public static PiController? PiController { get; private set; }
		public static GpioPinController? PinController { get; private set; }
		public static Updater Update { get; private set; } = new Updater();
		public static CoreConfig Config { get; set; } = new CoreConfig();
		public static ModuleInitializer ModuleLoader { get; private set; } = new ModuleInitializer();
		public static WeatherClient WeatherClient { get; private set; } = new WeatherClient();
		public static PushbulletClient PushbulletClient { get; private set; } = new PushbulletClient();
		public static CoreServerBase CoreServer { get; private set; } = new CoreServerBase();
		public static IFileWatcher FileWatcher { get; private set; } = new GenericFileWatcher();
		public static IModuleWatcher ModuleWatcher { get; private set; } = new GenericModuleWatcher();

		public static bool CoreInitiationCompleted { get; private set; }
		public static bool IsNetworkAvailable { get; set; }
		public static bool DisableFirstChanceLogWithDebug { get; set; }
		public static OSPlatform RunningPlatform { get; private set; }
		private static readonly SemaphoreSlim NetworkSemaphore = new SemaphoreSlim(1, 1);
		public static string AssistantName { get; set; } = "Tess Home Assistant";
		public static CancellationTokenSource KeepAliveToken { get; private set; } = new CancellationTokenSource(TimeSpan.MaxValue);

		/// <summary>
		/// Thread blocking method to startup the post init tasks.
		/// </summary>
		/// <returns>Boolean, when the endless thread block has been interrupted, such as, on exit.</returns>
		public static async Task PostInitTasks() {
			Logger.Log("Running post-initiation tasks...", LogLevels.Trace);
			await ModuleLoader.ExecuteAsyncEvent(ModuleInitializer.MODULE_EXECUTION_CONTEXT.AssistantStartup).ConfigureAwait(false);

			if (Config.DisplayStartupMenu) {
				await DisplayRelayCycleMenu().ConfigureAwait(false);
			}

			await TTS.AssistantVoice(TTS.ESPEECH_CONTEXT.AssistantStartup).ConfigureAwait(false);
			await KeepAlive().ConfigureAwait(false);
		}

		public Core VerifyStartupArgs(string[] args) {
			ParseStartupArguments(args);
			return this;
		}

		public Core RegisterEvents() {
			Logging.Logger.LogMessageReceived += Logger_LogMessageReceived;
			Logging.Logger.OnColoredReceived += Logger_OnColoredReceived;
			Logging.Logger.OnErrorReceived += Logger_OnErrorReceived;
			Logging.Logger.OnExceptionReceived += Logger_OnExceptionReceived;
			Logging.Logger.OnInputReceived += Logger_OnInputReceived;
			Logging.Logger.OnWarningReceived += Logger_OnWarningReceived;
			CoreServer.ServerStarted += CoreServer_ServerStarted;
			CoreServer.ServerShutdown += CoreServer_ServerShutdown;
			CoreServer.ClientConnected += CoreServer_ClientConnected;
			return this;
		}

		private void CoreServer_ClientConnected(object sender, Server.CoreServer.EventArgs.OnClientConnectedEventArgs e) {

		}

		private void CoreServer_ServerShutdown(object sender, Server.CoreServer.EventArgs.OnServerShutdownEventArgs e) {

		}

		private void CoreServer_ServerStarted(object sender, Server.CoreServer.EventArgs.OnServerStartedListerningEventArgs e) {

		}

		private void Logger_OnWarningReceived(object sender, Logging.EventArgs.EventArgsBase e) { }

		private void Logger_OnInputReceived(object sender, Logging.EventArgs.EventArgsBase e) { }

		private void Logger_OnExceptionReceived(object sender, Logging.EventArgs.OnExceptionMessageEventArgs e) {
			if (AssistantLoggers == null) {
				AssistantLoggers = new List<NLog.CoreLogger>();
			}

			NLog.CoreLogger? logger = AssistantLoggers.Find(x => !string.IsNullOrEmpty(x.LogIdentifier) && x.LogIdentifier.Equals(e.LogIdentifier, StringComparison.OrdinalIgnoreCase));

			if (logger == null) {
				logger = new NLog.CoreLogger(e.LogIdentifier);
				AssistantLoggers.Add(logger);
			}

			logger.Log(e.LogException, LogLevels.Exception, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
		}

		private void Logger_OnErrorReceived(object sender, Logging.EventArgs.EventArgsBase e) {
			if (AssistantLoggers == null) {
				AssistantLoggers = new List<NLog.CoreLogger>();
			}

			NLog.CoreLogger? logger = AssistantLoggers.Find(x => !string.IsNullOrEmpty(x.LogIdentifier) && x.LogIdentifier.Equals(e.LogIdentifier, StringComparison.OrdinalIgnoreCase));

			if (logger == null) {
				logger = new NLog.CoreLogger(e.LogIdentifier);
				AssistantLoggers.Add(logger);
			}

			logger.Log(e.LogMessage, LogLevels.Error, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
		}

		private void Logger_OnColoredReceived(object sender, Logging.EventArgs.WithColorEventArgs e) { }

		private void Logger_LogMessageReceived(object sender, Logging.EventArgs.LogMessageEventArgs e) {
			if (AssistantLoggers == null) {
				AssistantLoggers = new List<NLog.CoreLogger>();
			}

			NLog.CoreLogger? logger = AssistantLoggers.Find(x => !string.IsNullOrEmpty(x.LogIdentifier) && x.LogIdentifier.Equals(e.LogIdentifier, StringComparison.OrdinalIgnoreCase));

			if (logger == null) {
				logger = new NLog.CoreLogger(e.LogIdentifier);
				AssistantLoggers.Add(logger);
			}

			switch (e.LogLevel) {
				case LogLevels.Trace:
					logger.Log(e.LogMessage, LogLevels.Trace, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
					break;
				case LogLevels.Debug:
					logger.Log(e.LogMessage, LogLevels.Debug, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
					break;
				case LogLevels.Info:
					logger.Log(e.LogMessage, LogLevels.Info, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
					break;
				case LogLevels.Warn:
					logger.Log(e.LogMessage, LogLevels.Warn, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
					break;
				case LogLevels.Error:
				case LogLevels.Exception:
				case LogLevels.Fatal:
					break;
				case LogLevels.Green:
					logger.Log(e.LogMessage, LogLevels.Green, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
					break;
				case LogLevels.Red:
					logger.Log(e.LogMessage, LogLevels.Red, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
					break;
				case LogLevels.Blue:
					logger.Log(e.LogMessage, LogLevels.Blue, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
					break;
				case LogLevels.Cyan:
					logger.Log(e.LogMessage, LogLevels.Cyan, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
					break;
				case LogLevels.Magenta:
					logger.Log(e.LogMessage, LogLevels.Magenta, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
					break;
				case LogLevels.Input:
					logger.Log(e.LogMessage, LogLevels.Input, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
					break;
				case LogLevels.Custom:
					logger.Log(e.LogMessage, LogLevels.Custom, e.CallerMemberName, e.CallerLineNumber, e.CallerFilePath);
					break;
			}
		}

		public Core PreInitTasks() {
			if (File.Exists(Constants.TraceLogPath)) {
				File.Delete(Constants.TraceLogPath);
			}

			Helpers.SetFileSeperator();
			Helpers.CheckMultipleProcess();
			IsNetworkAvailable = Helpers.IsNetworkAvailable();

			if (!IsNetworkAvailable) {
				Logger.Log("No Internet connection.", LogLevels.Warn);
				Logger.Log($"Starting {AssistantName} in offline mode...");
			}

			OS.Init(true);
			return this;
		}

		public async Task<Core> LoadConfiguration() {
			await Config.LoadConfig().ConfigureAwait(false);
			return this;
		}

		public Core VariableAssignation() {
			StartupTime = DateTime.Now;
			AssistantName = Config.AssistantDisplayName;
			Logger.LogIdentifier = AssistantName;
			RunningPlatform = Helpers.GetOsPlatform();
			Config.ProgramLastStartup = StartupTime;
			Constants.LocalIP = Helpers.GetLocalIpAddress();
			Constants.ExternelIP = Helpers.GetExternalIp() ?? "-Invalid-";
			GpioCore = new Gpio.Gpio(EGPIO_DRIVERS.RaspberryIODriver, Config.CloseRelayOnShutdown)
				.InitGpioCore(Config.OutputModePins, Config.InputModePins);
			return this;
		}

		public async Task<Core> StartTcpServer(int port, int backlog) {
			await CoreServer.StartAsync(port, backlog).ConfigureAwait(false);
			return this;
		}

		public Core AllowLocalNetworkConnections() {
			SendLocalIp();
			return this;
		}

		public Core StartConsoleTitleUpdater() {
			Helpers.InBackgroundThread(SetConsoleTitle, "Console Title Updater", true);
			return this;
		}

		public Core DisplayASCIILogo() {
			Helpers.GenerateAsciiFromText(Config.AssistantDisplayName);
			return this;
		}

		public Core DisplayASCIILogo(string text) {
			if (!string.IsNullOrEmpty(text)) {
				Helpers.GenerateAsciiFromText(text);
			}
			return this;
		}

		public Core Misc() {
			File.WriteAllText("version.txt", Constants.Version?.ToString());
			Logger.Log($"X---------------- Starting {AssistantName} v{Constants.Version} ----------------X", LogLevels.Blue);

			return this;
		}

		public Core StartWatcher() {
			FileWatcher.InitWatcher(Constants.ConfigDirectory, new Dictionary<string, Action>() {
				{ "Assistant.json", new Action(OnCoreConfigChangeEvent) },
				{ "DiscordBot.json", new Action(OnDiscordConfigChangeEvent) },
				{ "MailConfig.json", new Action(OnMailConfigChangeEvent) }
			}, new List<string>(), "*.json", false);

			ModuleWatcher.InitWatcher(Constants.ModuleDirectory, new List<Action<string>>() {
				new Action<string>((x) => OnModuleDirectoryChangeEvent(x))
			}, new List<string>(), "*.dll", false);

			return this;
		}

		public Core StartPushBulletService() {
			if (!string.IsNullOrEmpty(Config.PushBulletApiKey)) {
				Helpers.InBackground(() => {
					if (PushbulletClient.InitPushbulletClient(Config.PushBulletApiKey) != null) {
						Logger.Log("Push bullet service started.", LogLevels.Trace);
					}
				});
			}

			return this;
		}

		public Core CheckAndUpdate() {
			Helpers.InBackground(async () => await Update.CheckAndUpdateAsync(true).ConfigureAwait(false));
			return this;
		}

		public Core StartModules() {
			if (Config.EnableModules) {
				Helpers.InBackground(async () => {
					await ModuleLoader.LoadAsync().ConfigureAwait(false);
				});
			}

			return this;
		}

		public Core StartPinController() {
			if (GpioCore != null) {
				PiController = GpioCore.GpioController;
				PinController = GpioCore.PinController;
			}

			if (PiController != null && !PiController.IsControllerProperlyInitialized) {
				PiController = null;
				Logger.Warning("Failed to start gpio pin controller.");
			}

			return this;
		}

		public Core MarkInitializationCompletion() {
			CoreInitiationCompleted = true;
			return this;
		}

		//private static void TcpServerBase_ClientConnected(object sender, OnClientConnectedEventArgs e) {
		//	lock (ClientManagers) {
		//		ClientManagers.TryAdd(e.ClientUid, new TcpServerClientManager(e.ClientUid));
		//	}
		//}

		//private static void TcpServerBase_ServerStarted(object sender, OnServerStartedListerningEventArgs e) => Logger.Log($"TCP Server listening at {e.ListerningAddress} / {e.ServerPort}");

		//private static void TcpServerBase_ServerShutdown(object sender, OnServerShutdownEventArgs e) => Logger.Log($"TCP shutting down.");

		private static void SetConsoleTitle() {
			Helpers.SetConsoleTitle($"http://{Constants.LocalIP}:9090/ | {DateTime.Now.ToLongTimeString()} | Uptime: {Math.Round(Pi.Info.UptimeTimeSpan.TotalMinutes, 3)} minutes");

			if (RefreshConsoleTitleTimer == null) {
				RefreshConsoleTitleTimer = new Timer(e => SetConsoleTitle(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
			}
		}

		private static async Task DisplayConsoleCommandMenu() {
			Logger.Log("Displaying console command window", LogLevels.Trace);
			Logger.Log($"------------------------- COMMAND WINDOW -------------------------", LogLevels.Input);
			Logger.Log($"{Constants.ConsoleShutdownKey} - Shutdown assistant.", LogLevels.Input);

			if (!PiController.IsAllowedToExecute) {
				Logger.Log($"{Constants.ConsoleRelayCommandMenuKey} - Display relay pin control menu.", LogLevels.Input);
				Logger.Log($"{Constants.ConsoleRelayCycleMenuKey} - Display relay cycle control menu.", LogLevels.Input);

				if (Config.EnableModules) {
					Logger.Log($"{Constants.ConsoleModuleShutdownKey} - Invoke shutdown method on all currently running modules.", LogLevels.Input);
				}

				Logger.Log($"{Constants.ConsoleMorseCodeKey} - Morse code generator for the specified text.", LogLevels.Input);
			}

			Logger.Log($"{Constants.ConsoleTestMethodExecutionKey} - Run preconfigured test methods or tasks.", LogLevels.Input);
			if (WeatherClient != null) {
				Logger.Log($"{Constants.ConsoleWeatherInfoKey} - Get weather info of the specified location based on the pin code.", LogLevels.Input);
			}

			Logger.Log($"-------------------------------------------------------------------", LogLevels.Input);
			Logger.Log("Awaiting user input: \n", LogLevels.Input);

			int failedTriesCount = 0;
			int maxTries = 3;

			while (true) {
				if (failedTriesCount > maxTries) {
					Logger.Log($"Multiple wrong inputs. please start the command menu again  by pressing {Constants.ConsoleCommandMenuKey} key.", LogLevels.Warn);
					return;
				}

				char pressedKey = Console.ReadKey().KeyChar;

				switch (pressedKey) {
					case Constants.ConsoleShutdownKey:
						Logger.Log("Shutting down assistant...", LogLevels.Warn);
						await Task.Delay(1000).ConfigureAwait(false);
						await Exit(0).ConfigureAwait(false);
						return;

					case Constants.ConsoleRelayCommandMenuKey when !PiController.IsAllowedToExecute:
						Logger.Log("Displaying relay command menu...", LogLevels.Warn);
						DisplayRelayCommandMenu();
						return;

					case Constants.ConsoleRelayCycleMenuKey when !PiController.IsAllowedToExecute:
						Logger.Log("Displaying relay cycle menu...", LogLevels.Warn);
						await DisplayRelayCycleMenu().ConfigureAwait(false);
						return;

					case Constants.ConsoleRelayCommandMenuKey when PiController.IsAllowedToExecute:
					case Constants.ConsoleRelayCycleMenuKey when PiController.IsAllowedToExecute:
						Logger.Log("Assistant is running in an Operating system/Device which doesn't support GPIO pin controlling functionality.", LogLevels.Warn);
						return;

					case Constants.ConsoleMorseCodeKey when !PiController.IsAllowedToExecute:
						if (PiController == null) {
							return;
						}

						Logger.Log("Enter text to convert to Morse: ");
						string morseCycle = Console.ReadLine();
						var morseTranslator = PiController.GetMorseTranslator();

						if (morseTranslator == null || !morseTranslator.IsTranslatorOnline) {
							Logger.Warning("Morse translator is offline or unavailable.");
							return;
						}

						await morseTranslator.RelayMorseCycle(morseCycle, Config.OutputModePins[0]).ConfigureAwait(false);
						return;

					case Constants.ConsoleWeatherInfoKey:
						Logger.Log("Please enter the pin code of the location: ");
						int counter = 0;

						int pinCode;
						while (true) {
							if (counter > 4) {
								Logger.Log("Failed multiple times. aborting...");
								return;
							}

							try {
								pinCode = Convert.ToInt32(Console.ReadLine());
								break;
							}
							catch {
								counter++;
								Logger.Log("Please try again!", LogLevels.Warn);
								continue;
							}
						}

						if (string.IsNullOrEmpty(Config.OpenWeatherApiKey)) {
							Logger.Warning("Weather api key cannot be null.");
							return;
						}

						if (WeatherClient == null) {
							Logger.Warning("Weather client is not initiated.");
							return;
						}

						WeatherResponse? response = await WeatherClient.GetWeather(Config.OpenWeatherApiKey, pinCode, "in").ConfigureAwait(false);

						if (response == null) {
							Logger.Warning("Failed to fetch weather response.");
							return;
						}

						Logger.Log($"------------ Weather information for {pinCode}/{response.LocationName} ------------", LogLevels.Green);

						if (response.Data != null) {
							Logger.Log($"Temperature: {response.Data.Temperature}", LogLevels.Green);
							Logger.Log($"Humidity: {response.Data.Humidity}", LogLevels.Green);
							Logger.Log($"Pressure: {response.Data.Pressure}", LogLevels.Green);
						}

						if (response.Wind != null) {
							Logger.Log($"Wind speed: {response.Wind.Speed}", LogLevels.Green);
						}

						if (response.Location != null) {
							Logger.Log($"Latitude: {response.Location.Latitude}", LogLevels.Green);
							Logger.Log($"Longitude: {response.Location.Longitude}", LogLevels.Green);
							Logger.Log($"Location name: {response.LocationName}", LogLevels.Green);
						}

						return;

					case Constants.ConsoleTestMethodExecutionKey:
						Logger.Log("Executing test methods/tasks", LogLevels.Warn);
						Logger.Log("Test method execution finished successfully!", LogLevels.Green);
						return;

					case Constants.ConsoleModuleShutdownKey when ModuleLoader.Modules.Count > 0 && Config.EnableModules:
						Logger.Log("Shutting down all modules...", LogLevels.Warn);
						ModuleLoader.OnCoreShutdown();
						return;

					case Constants.ConsoleModuleShutdownKey when ModuleLoader.Modules.Count <= 0:
						Logger.Log("There are no modules to shutdown...");
						return;

					default:
						if (failedTriesCount > maxTries) {
							Logger.Log($"Unknown key was pressed. ({maxTries - failedTriesCount} tries left)", LogLevels.Warn);
						}

						failedTriesCount++;
						continue;
				}
			}
		}

		private static async Task KeepAlive() {
			Logger.Log($"Press {Constants.ConsoleCommandMenuKey} for the console command menu.", LogLevels.Green);

			while (!KeepAliveToken.Token.IsCancellationRequested) {
				char pressedKey = Console.ReadKey().KeyChar;

				switch (pressedKey) {
					case Constants.ConsoleCommandMenuKey:
						await DisplayConsoleCommandMenu().ConfigureAwait(false);
						break;

					default:
						Logger.Log("Unknown key pressed during KeepAlive() command", LogLevels.Trace);
						continue;
				}
			}
		}

		private static void ParseStartupArguments(string[] args) {
			if (!args.Any() || args == null) {
				return;
			}

			Parser.Default.ParseArguments<Options>(args).WithParsed(x => {
				if (x.Debug) {
					Logger.Log("Debug mode enabled. Logging trace data to console.", LogLevels.Warn);
					Config.Debug = true;
				}

				if (x.Safe) {
					Logger.Log("Safe mode enabled. Only preconfigured gpio pins are allowed to be modified.", LogLevels.Warn);
					Config.GpioSafeMode = true;
				}

				if (x.EnableFirstChance) {
					Logger.Log("First chance exception logging is enabled.", LogLevels.Warn);
					Config.EnableFirstChanceLog = true;
				}

				if (x.TextToSpeech) {
					Logger.Log("Enabled text to speech service via startup arguments.", LogLevels.Warn);
					Config.EnableTextToSpeech = true;
				}

				if (x.DisableFirstChance) {
					Logger.Log("Disabling first chance exception logging with debug mode.", LogLevels.Warn);
					DisableFirstChanceLogWithDebug = true;
				}
			});
		}

		private static void DisplayRelayCommandMenu() {
			Logger.Log("-------------------- RELAY COMMAND MENU --------------------", LogLevels.Input);
			Logger.Log("1 | Relay pin 1", LogLevels.Input);
			Logger.Log("2 | Relay pin 2", LogLevels.Input);
			Logger.Log("3 | Relay pin 3", LogLevels.Input);
			Logger.Log("4 | Relay pin 4", LogLevels.Input);
			Logger.Log("5 | Relay pin 5", LogLevels.Input);
			Logger.Log("6 | Relay pin 6", LogLevels.Input);
			Logger.Log("7 | Relay pin 7", LogLevels.Input);
			Logger.Log("8 | Relay pin 8", LogLevels.Input);
			Logger.Log("9 | Schedule task for specified relay pin", LogLevels.Input);
			Logger.Log("0 | Exit menu", LogLevels.Input);
			Logger.Log("Press any key (between 0 - 9) for their respective option.\n", LogLevels.Input);
			ConsoleKeyInfo key = Console.ReadKey();
			Logger.Log("\n", LogLevels.Input);

			if (!int.TryParse(key.KeyChar.ToString(), out int SelectedValue)) {
				Logger.Log("Could not parse the input key. please try again!", LogLevels.Error);
				Logger.Log("Command menu closed.");
				Logger.Log($"Press {Constants.ConsoleCommandMenuKey} for the console command menu.", LogLevels.Green);
				return;
			}

			static void set(int pin) {
				if (PiController == null || PinController == null) {
					return;
				}

				GpioPinConfig? pinStatus = PinController.GetGpioConfig(pin);
				if (pinStatus.IsPinOn) {
					PinController.SetGpioValue(pin, GpioPinMode.Output, GpioPinState.Off);
					Logger.Log($"Successfully set {pin} pin to OFF.", LogLevels.Green);
				}
				else {
					PinController.SetGpioValue(pin, GpioPinMode.Output, GpioPinState.On);
					Logger.Log($"Successfully set {pin} pin to ON.", LogLevels.Green);
				}
			}

			switch (SelectedValue) {
				case 1:
					set(Config.RelayPins[0]);
					break;

				case 2:
					set(Config.RelayPins[1]);
					break;

				case 3:
					set(Config.RelayPins[2]);
					break;

				case 4:
					set(Config.RelayPins[3]);
					break;

				case 5:
					set(Config.RelayPins[4]);
					break;

				case 6:
					set(Config.RelayPins[5]);
					break;

				case 7:
					set(Config.RelayPins[6]);
					break;

				case 8:
					set(Config.RelayPins[7]);
					break;

				case 9:
					Logger.Log("Please enter the pin u want to configure: ", LogLevels.Input);
					string pinNumberKey = Console.ReadLine();

					if (!int.TryParse(pinNumberKey, out int pinNumber) || Convert.ToInt32(pinNumberKey) <= 0) {
						Logger.Log("Your entered pin number is incorrect. please enter again.", LogLevels.Input);

						pinNumberKey = Console.ReadLine();
						if (!int.TryParse(pinNumberKey, out pinNumber) || Convert.ToInt32(pinNumberKey) <= 0) {
							Logger.Log("Your entered pin number is incorrect again. press m for menu, and start again!", LogLevels.Input);
							return;
						}
					}

					Logger.Log("Please enter the amount of delay you want in between the task. (in minutes)", LogLevels.Input);
					string delayInMinuteskey = Console.ReadLine();
					if (!int.TryParse(delayInMinuteskey, out int delay) || Convert.ToInt32(delayInMinuteskey) <= 0) {
						Logger.Log("Your entered delay is incorrect. please enter again.", LogLevels.Input);

						delayInMinuteskey = Console.ReadLine();
						if (!int.TryParse(delayInMinuteskey, out delay) || Convert.ToInt32(delayInMinuteskey) <= 0) {
							Logger.Log("Your entered pin is incorrect again. press m for menu, and start again!", LogLevels.Input);
							return;
						}
					}

					Logger.Log("Please enter the status u want the task to configure: (0 = OFF, 1 = ON)", LogLevels.Input);

					string pinStatuskey = Console.ReadLine();
					if (!int.TryParse(pinStatuskey, out int pinStatus) || (Convert.ToInt32(pinStatuskey) != 0 && Convert.ToInt32(pinStatus) != 1)) {
						Logger.Log("Your entered pin status is incorrect. please enter again.", LogLevels.Input);

						pinStatuskey = Console.ReadLine();
						if (!int.TryParse(pinStatuskey, out pinStatus) || (Convert.ToInt32(pinStatuskey) != 0 && Convert.ToInt32(pinStatus) != 1)) {
							Logger.Log("Your entered pin status is incorrect again. press m for menu, and start again!", LogLevels.Input);
							return;
						}
					}

					if (PiController == null || PinController == null) {
						return;
					}

					GpioPinConfig status = PinController.GetGpioConfig(pinNumber);

					if (status.IsPinOn && pinStatus.Equals(1)) {
						Logger.Log("Pin is already configured to be in ON State. Command doesn't make any sense.");
						return;
					}

					if (!status.IsPinOn && pinStatus.Equals(0)) {
						Logger.Log("Pin is already configured to be in OFF State. Command doesn't make any sense.");
						return;
					}

					if (Config.IRSensorPins.Count() > 0 && Config.IRSensorPins.Contains(pinNumber)) {
						Logger.Log("Sorry, the specified pin is preconfigured for IR Sensor. cannot modify!");
						return;
					}

					if (!Config.RelayPins.Contains(pinNumber)) {
						Logger.Log("Sorry, the specified pin doesn't exist in the relay pin category.");
						return;
					}

					Helpers.ScheduleTask(() => {
						if (status.IsPinOn && pinStatus.Equals(0)) {
							PinController.SetGpioValue(pinNumber, GpioPinMode.Output, GpioPinState.Off);
							Logger.Log($"Successfully finished execution of the task: {pinNumber} pin set to OFF.", LogLevels.Green);
						}

						if (!status.IsPinOn && pinStatus.Equals(1)) {
							PinController.SetGpioValue(pinNumber, GpioPinMode.Output, GpioPinState.On);
							Logger.Log($"Successfully finished execution of the task: {pinNumber} pin set to ON.", LogLevels.Green);
						}
					}, TimeSpan.FromMinutes(delay));

					Logger.Log(
						pinStatus.Equals(0)
							? $"Successfully scheduled a task: set {pinNumber} pin to OFF"
							: $"Successfully scheduled a task: set {pinNumber} pin to ON", LogLevels.Green);
					break;
			}

			Logger.Log("Command menu closed.");
			Logger.Log($"Press {Constants.ConsoleCommandMenuKey} for the console command menu.", LogLevels.Green);
		}

		private static async Task DisplayRelayCycleMenu() {
			if (!PiController.IsAllowedToExecute) {
				Logger.Log("You are running on incorrect OS or device. Pi controls are disabled.", LogLevels.Error);
				return;
			}

			Logger.Log("--------------------MODE MENU--------------------", LogLevels.Input);
			Logger.Log("1 | Relay Cycle", LogLevels.Input);
			Logger.Log("2 | Relay OneMany", LogLevels.Input);
			Logger.Log("3 | Relay OneOne", LogLevels.Input);
			Logger.Log("4 | Relay OneTwo", LogLevels.Input);
			Logger.Log("5 | Relay Single", LogLevels.Input);
			Logger.Log("6 | Relay Default", LogLevels.Input);
			Logger.Log("0 | Exit", LogLevels.Input);
			Logger.Log("Press any key (between 0 - 6) for their respective option.\n", LogLevels.Input);
			ConsoleKeyInfo key = Console.ReadKey();
			Logger.Log("\n", LogLevels.Input);

			if (!int.TryParse(key.KeyChar.ToString(), out int SelectedValue)) {
				Logger.Log("Could not parse the input key. please try again!", LogLevels.Error);
				Logger.Log($"Press {Constants.ConsoleCommandMenuKey} for command menu.", LogLevels.Info);
				return;
			}

			if (PiController == null || PinController == null) {
				return;
			}

			bool Configured;
			switch (SelectedValue) {
				case 1:
					Configured = await PinController.RelayTestServiceAsync(Config.RelayPins, GpioCycles.Cycle).ConfigureAwait(false);

					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}

					break;

				case 2:
					Configured = await PinController.RelayTestServiceAsync(Config.RelayPins, GpioCycles.OneMany).ConfigureAwait(false);

					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}

					break;

				case 3:
					Configured = await PinController.RelayTestServiceAsync(Config.RelayPins, GpioCycles.OneOne).ConfigureAwait(false);
					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}
					break;

				case 4:
					Configured = await PinController.RelayTestServiceAsync(Config.RelayPins, GpioCycles.OneTwo).ConfigureAwait(false);

					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}
					break;

				case 5:
					Logger.Log("\nPlease select the channel (3, 4, 17, 2, 27, 10, 22, 9, etc): ", LogLevels.Input);
					string singleKey = Console.ReadLine();

					if (!int.TryParse(singleKey, out int selectedsingleKey)) {
						Logger.Log("Could not parse the input key. please try again!", LogLevels.Error);
						goto case 5;
					}

					Configured = await PinController.RelayTestServiceAsync(Config.RelayPins, GpioCycles.Single, selectedsingleKey).ConfigureAwait(false);

					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}
					break;

				case 6:
					Configured = await PinController.RelayTestServiceAsync(Config.RelayPins, GpioCycles.Default).ConfigureAwait(false);

					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}
					break;

				case 0:
					Logger.Log("Exiting from menu...", LogLevels.Input);
					return;

				default:
					goto case 0;
			}

			Logger.Log(Configured ? "Test successful!" : "Test Failed!");

			Logger.Log("Relay menu closed.");
			Logger.Log($"Press {Constants.ConsoleCommandMenuKey} to display command menu.");
		}

		public static async Task OnNetworkDisconnected() {
			try {
				await NetworkSemaphore.WaitAsync().ConfigureAwait(false);
				IsNetworkAvailable = false;
				await ModuleLoader.ExecuteAsyncEvent(MODULE_EXECUTION_CONTEXT.NetworkDisconnected).ConfigureAwait(false);
				Constants.ExternelIP = "Internet connection lost.";

				if (Update != null) {
					Update.StopUpdateTimer();
					Logger.Log("Stopped update timer.", LogLevels.Warn);
				}
			}
			finally {
				NetworkSemaphore.Release();
			}
		}

		public static async Task OnNetworkReconnected() {
			try {
				await NetworkSemaphore.WaitAsync().ConfigureAwait(false);
				IsNetworkAvailable = true;
				await ModuleLoader.ExecuteAsyncEvent(MODULE_EXECUTION_CONTEXT.NetworkReconnected).ConfigureAwait(false);
				Constants.ExternelIP = Helpers.GetExternalIp();

				if (Config.AutoUpdates && IsNetworkAvailable) {
					Logger.Log("Checking for any new version...", LogLevels.Trace);
					File.WriteAllText("version.txt", Constants.Version?.ToString());
					await Update.CheckAndUpdateAsync(true).ConfigureAwait(false);
				}
			}
			finally {
				NetworkSemaphore.Release();
			}
		}

		public static async Task OnExit() {
			Logger.Log("Shutting down...");

			if (ModuleLoader != null) {
				await ModuleLoader.ExecuteAsyncEvent(MODULE_EXECUTION_CONTEXT.AssistantShutdown).ConfigureAwait(false);
			}

			PiController?.InitGpioShutdownTasks();
			Update?.StopUpdateTimer();
			RefreshConsoleTitleTimer?.Dispose();
			FileWatcher.StopWatcher();
			ModuleWatcher.StopWatcher();

			if (CoreServer.IsServerListerning) {
				await CoreServer.TryShutdownAsync().ConfigureAwait(false);
			}

			//if (KestrelServer.IsServerOnline) {
			//	await KestrelServer.Stop().ConfigureAwait(false);
			//}

			ModuleLoader?.OnCoreShutdown();
			Config.ProgramLastShutdown = DateTime.Now;
			await Config.SaveConfig(Config).ConfigureAwait(false);
			Logger.Log("Finished on exit tasks.", LogLevels.Trace);
		}

		public static async Task Exit(int exitCode = 0) {
			if (exitCode != 0) {
				Logger.Log("Exiting with nonzero error code...", LogLevels.Error);
			}

			if (exitCode == 0) {
				await OnExit().ConfigureAwait(false);
			}

			Logger.Log("Bye, have a good day sir!");
			NLog.NLog.LoggerOnShutdown();
			KeepAliveToken.Cancel();
			Environment.Exit(exitCode);
		}

		public static async Task Restart(int delay = 10) {
			if (!Config.AutoRestart) {
				Logger.Log("Auto restart is turned off in config.", LogLevels.Warn);
				return;
			}

			Helpers.ScheduleTask(() => "cd /home/pi/Desktop/HomeAssistant/Helpers/Restarter && dotnet RestartHelper.dll".ExecuteBash(true), TimeSpan.FromSeconds(delay));
			await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
			await Exit(0).ConfigureAwait(false);
		}

		public static async Task SystemShutdown() {
			await ModuleLoader.ExecuteAsyncEvent(MODULE_EXECUTION_CONTEXT.SystemShutdown).ConfigureAwait(false);
			if (PiController.IsAllowedToExecute) {
				Logger.Log($"Assistant is running on raspberry pi.", LogLevels.Trace);
				Logger.Log("Shutting down pi...", LogLevels.Warn);
				await OnExit().ConfigureAwait(false);
				await Pi.ShutdownAsync().ConfigureAwait(false);
				return;
			}

			if (Helpers.GetOsPlatform() == OSPlatform.Windows) {
				Logger.Log($"Assistant is running on a windows system.", LogLevels.Trace);
				Logger.Log("Shutting down system...", LogLevels.Warn);
				await OnExit().ConfigureAwait(false);
				ProcessStartInfo psi = new ProcessStartInfo("shutdown", "/s /t 0") {
					CreateNoWindow = true,
					UseShellExecute = false
				};
				Process.Start(psi);
			}
		}

		public static async Task SystemRestart() {
			await ModuleLoader.ExecuteAsyncEvent(MODULE_EXECUTION_CONTEXT.SystemRestart).ConfigureAwait(false);
			if (PiController.IsAllowedToExecute) {
				Logger.Log($"Assistant is running on raspberry pi.", LogLevels.Trace);
				Logger.Log("Restarting pi...", LogLevels.Warn);
				await OnExit().ConfigureAwait(false);
				await Pi.RestartAsync().ConfigureAwait(false);
				return;
			}

			if (Helpers.GetOsPlatform() == OSPlatform.Windows) {
				Logger.Log($"Assistant is running on a windows system.", LogLevels.Trace);
				Logger.Log("Restarting system...", LogLevels.Warn);
				await OnExit().ConfigureAwait(false);
				ProcessStartInfo psi = new ProcessStartInfo("shutdown", "/r /t 0") {
					CreateNoWindow = true,
					UseShellExecute = false
				};
				Process.Start(psi);
			}
		}

		private void OnCoreConfigChangeEvent() {
			if (!File.Exists(Constants.CoreConfigPath)) {
				Logger.Log("The core config file has been deleted.", LogLevels.Warn);
				Logger.Log("Fore quitting assistant.", LogLevels.Warn);
				Task.Run(async () => await Exit(0).ConfigureAwait(false));
			}

			Logger.Log("Updating core config as the local config file as been updated...");
			Helpers.InBackgroundThread(async () => await Config.LoadConfig().ConfigureAwait(false));
		}

		private void OnDiscordConfigChangeEvent() {
			//TODO: Discord config file change events
		}

		private void OnMailConfigChangeEvent() {
			//TODO: Mail config file change events
		}

		private void OnModuleDirectoryChangeEvent(string? absoluteFileName) {
			if (string.IsNullOrEmpty(absoluteFileName)) {
				return;
			}

			string fileName = Path.GetFileName(absoluteFileName);
			string filePath = Path.GetFullPath(absoluteFileName);
			Logger.Log($"An event has been raised on module folder for file > {fileName}", LogLevels.Trace);

			if (!File.Exists(filePath)) {
				ModuleLoader.UnloadFromPath(filePath);
				return;
			}

			Helpers.InBackground(async () => await ModuleLoader.LoadAsync().ConfigureAwait(false));
		}

		/// <summary>
		/// The method sends the current working local ip to an central server which i personally use for such tasks and for authentication etc.
		/// You have to specify such a server manually else contact me personally for my server IP.
		/// We use this so that the mobile controller app of the assistant can connect to the assistant running on the connected local interface.
		/// </summary>
		/// <param name="enableRecrussion">Specify if you want to execute this method in a loop every SendIpDelay minutes. (recommended)</param>
		private static void SendLocalIp() {
			string? localIp = Helpers.GetLocalIpAddress();

			if (localIp == null || string.IsNullOrEmpty(localIp)) {
				return;
			}

			Constants.LocalIP = localIp;
			RestClient client = new RestClient($"http://{Config.StatisticsServerIP}/api/v1/assistant/ip?ip={Constants.LocalIP}");
			RestRequest request = new RestRequest(RestSharp.Method.POST);
			request.AddHeader("cache-control", "no-cache");
			IRestResponse response = client.Execute(request);

			if (response.StatusCode != HttpStatusCode.OK) {
				Logger.Log("Failed to download. Status Code: " + response.StatusCode + "/" + response.ResponseStatus);
			}

			Logger.Log($"{Constants.LocalIP} IP request send!", LogLevels.Trace);
		}
	}
}
