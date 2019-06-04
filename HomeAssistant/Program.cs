using CommandLine;
using HomeAssistant.Core;
using HomeAssistant.Extensions;
using HomeAssistant.Log;
using HomeAssistant.Modules;
using HomeAssistant.Server;
using HomeAssistant.Update;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Unosquare.RaspberryIO;
using Unosquare.RaspberryIO.Abstractions;
using Unosquare.WiringPi;
using static HomeAssistant.Core.Enums;

namespace HomeAssistant {
	public class Options {
		[Option('d', "debug", Required = false, HelpText = "Displays all Trace level messages to console. (for debugging)")]
		public bool Debug { get; set; }

		[Option('s', "safe", Required = false, HelpText = "Enables safe mode so that only pre configured pins can be modified.")]
		public bool Safe { get; set; }
	}

	public class Program {
		private static Logger Logger;
		public static GPIOController Controller;
		public static Updater Update = new Updater();
		public static TCPServer CoreServer = new TCPServer();
		public static CoreConfig Config = new CoreConfig();
		private static GPIOConfigHandler GPIOConfigHandler = new GPIOConfigHandler();
		private static GPIOConfigRoot GPIORootObject = new GPIOConfigRoot();
		private static List<GPIOPinConfig> GPIOConfig = new List<GPIOPinConfig>();
		public static ModuleInitializer Modules;

		public static readonly string ProcessFileName = Process.GetCurrentProcess().MainModule.FileName;
		public static DateTime StartupTime;
		private static Timer RefreshConsoleTitleTimer;
		public static bool CoreInitiationCompleted = false;
		public static bool DisablePiMethods = false;

		// Handle Pre-init Tasks in here
		private static async Task Main(string[] args) {
			Helpers.CheckMultipleProcess();
			Logger = new Logger("CORE");
			TaskScheduler.UnobservedTaskException += HandleTaskExceptions;
			AppDomain.CurrentDomain.UnhandledException += HandleUnhandledExceptions;
			AppDomain.CurrentDomain.FirstChanceException += HandleFirstChanceExceptions;
			StartupTime = DateTime.Now;
			bool Init = await InitCore(args).ConfigureAwait(false);
		}

		private static async Task<bool> InitCore(string[] args) {
			try {
				await Helpers.DisplayTessASCII().ConfigureAwait(false);
				Constants.ExternelIP = Helpers.GetExternalIP();
				Helpers.InBackgroundThread(SetConsoleTitle, "Console Title Updater");
				Logger.Log($"X--------  Starting TESS Assistant v{Constants.Version}  --------X", LogLevels.Ascii);
				Logger.Log("Loading core config...", LogLevels.Trace);
				try {
					Config = Config.LoadConfig();
				}
				catch (NullReferenceException) {
					Logger.Log("Fatal error has occured during loading Core Config. exiting...", LogLevels.Error);
					await Exit(1).ConfigureAwait(false);
					return false;
				}

				ParseStartupArguments(args);

				if (!Helpers.IsRaspberryEnvironment()) {
					DisablePiMethods = true;
				}

				Logger.Log("Loading GPIO config...", LogLevels.Trace);
				try {
					GPIORootObject = GPIOConfigHandler.LoadConfig();

					if (GPIORootObject != null) {
						GPIOConfig = GPIORootObject.GPIOData;
					}
				}
				catch (NullReferenceException) {
					Logger.Log("Fatal error has occured during loading GPIO Config. exiting...", LogLevels.Error);
					await Exit(1).ConfigureAwait(false);
					return false;
				}

				Config.ProgramLastStartup = StartupTime;
				Logger.Log("Verifying internet connectivity...", LogLevels.Trace);

				if (Helpers.CheckForInternetConnection()) {
					Logger.Log("Internet connection verified!", LogLevels.Trace);
				}
				else {
					Logger.Log("No internet connection detected!");
					Logger.Log("To continue with initialization, press c else press q to quit.");
					ConsoleKeyInfo key = Console.ReadKey();

					switch (key.KeyChar) {
						case 'c':
							break;

						case 'q':
							await Exit(0).ConfigureAwait(false);
							break;

						default:
							Logger.Log("Unknown value entered! continuing to run the program...");
							goto case 'c';
					}
				}

				bool Token = false;

				try {
					string checkForToken = Helpers.FetchVariable(0, true, "GITHUB_TOKEN");

					if (string.IsNullOrEmpty(checkForToken) || string.IsNullOrWhiteSpace(checkForToken)) {
						Logger.Log("Github token isnt found. Updates will be disabled.", LogLevels.Warn);
						Token = false;
					}
				}
				catch (Exception) {
					Logger.Log("Github token isnt found. Updates will be disabled.", LogLevels.Warn);
					Token = false;
				}

				Token = true;

				if (Token && Config.AutoUpdates) {
					Logger.Log("Checking for any new version...", LogLevels.Trace);
					File.WriteAllText("version.txt", Constants.Version.ToString());
					await Update.CheckAndUpdate(true).ConfigureAwait(false);
				}

				if (Config.TCPServer) {
					_ = await CoreServer.StartServer().ConfigureAwait(false);
				}

				Modules = new ModuleInitializer();
				await Modules.StartModules().ConfigureAwait(false);
				await PostInitTasks(args).ConfigureAwait(false);
			}
			catch (Exception e) {
				Logger.Log(e, ExceptionLogLevels.Fatal);
				return false;
			}
			return true;
		}

		private static async Task PostInitTasks(string[] args) {
			Logger.Log("Running post-initiation tasks...", LogLevels.Trace);

			if (!DisablePiMethods) {
				Pi.Init<BootstrapWiringPi>();
				Controller = new GPIOController(args, GPIORootObject, GPIOConfig, GPIOConfigHandler);
				Controller.DisplayPiInfo();
				Logger.Log("Sucessfully Initiated Pi Configuration!");
			}
			else {
				Logger.Log("Disabled Raspberry Pi related methods and initiation tasks.");
			}

			CoreInitiationCompleted = true;

			if (Config.DisplayStartupMenu) {
				await DisplayRelayMenu().ConfigureAwait(false);
			}

			Logger.Log("Waiting for commands...");
			await KeepAlive('q', 'm', 'e', 'i', 't', 'c').ConfigureAwait(false);
		}

		private static void SetConsoleTitle() {
			Helpers.SetConsoleTitle($"{Helpers.TimeRan()} | {Constants.ExternelIP}:{Config.ServerPort} | {DateTime.Now.ToLongTimeString()} | Uptime: {Math.Round(Pi.Info.UptimeTimeSpan.TotalMinutes, 3)} minutes");

			if (RefreshConsoleTitleTimer == null) {
				RefreshConsoleTitleTimer = new Timer(e => SetConsoleTitle(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
			}
		}

		private static async Task KeepAlive(char loopBreaker = 'q', char menuKey = 'm', char quickShutDown = 'e', char imapIdleShutdown = 'i', char testKey = 't', char commandKey = 'c') {
			Logger.Log($"Press {loopBreaker} to quit in 5 seconds.");
			Logger.Log($"Press {quickShutDown} to exit application immediately.");
			Logger.Log($"Press {menuKey} to display GPIO menu.");
			Logger.Log($"Press {imapIdleShutdown} to stop all IMAP Idle notifications.");
			Logger.Log($"Press {testKey} to execute the TEST methods.");
			Logger.Log($"Press {commandKey} to display command menu.");

			while (true) {
				char pressedKey = Console.ReadKey().KeyChar;

				if (pressedKey.Equals(loopBreaker)) {
					Logger.Log("Exiting in 5 secs as per the admin.");
					await Task.Delay(5000).ConfigureAwait(false);
					await Exit(0).ConfigureAwait(false);
				}
				else if (pressedKey.Equals(menuKey)) {
					Logger.Log("Displaying relay testing menu...", LogLevels.Trace);
					await DisplayRelayMenu().ConfigureAwait(false);
					continue;
				}
				else if (pressedKey.Equals(quickShutDown)) {
					Logger.Log("Exiting...");
					await Exit(0).ConfigureAwait(false);
				}
				else if (pressedKey.Equals(imapIdleShutdown)) {
					Logger.Log("Exiting all email account imap idle...");
					Modules.DisposeAllEmailClients();
				}
				else if (pressedKey.Equals(testKey)) {
					Logger.Log("Running pre-configured tests...");
					Logger.Log("Setting Timer for charger controller.");
					Logger.Log("Enter 0 for OFF and 1 for ON", LogLevels.UserInput);
					char key = Console.ReadKey().KeyChar;
					Controller.ChargerController(Config.RelayPins[0], TimeSpan.FromMinutes(2), key == 0 ? GpioPinValue.High : GpioPinValue.Low);
					Logger.Log("No test tasks pending...");
				}
				else if (pressedKey.Equals(commandKey)) {
					DisplayCommandMenu();
				}
				else {
					continue;
				}
			}
		}

		private static void ParseStartupArguments(string[] args) {
			if (args.Count() <= 0 || args == null) {
				return;
			}

			Parser.Default.ParseArguments<Options>(args).WithParsed(x => {
				if (x.Debug) {
					Logger.Log("Debug mode enabled. Logging trace data to console.");
					Config.Debug = true;
				}

				if (x.Safe) {
					Logger.Log("Safe mode enabled. Only pre-configured gpio pins are allowed to be modified.");
					Config.GPIOSafeMode = true;
				}
				
			});
		}

		public static void DisplayCommandMenu() {
			Logger.Log("--------------------COMMAND MENU--------------------", LogLevels.UserInput);
			Logger.Log("1 | Relay pin 1", LogLevels.UserInput);
			Logger.Log("2 | Relay pin 2", LogLevels.UserInput);
			Logger.Log("3 | Relay pin 3", LogLevels.UserInput);
			Logger.Log("4 | Relay pin 4", LogLevels.UserInput);
			Logger.Log("5 | Relay pin 5", LogLevels.UserInput);
			Logger.Log("6 | Relay pin 6", LogLevels.UserInput);
			Logger.Log("7 | Relay pin 7", LogLevels.UserInput);
			Logger.Log("8 | Relay pin 8", LogLevels.UserInput);
			Logger.Log("9 | Schedule task for specified relay pin", LogLevels.UserInput);
			Logger.Log("0 | Exit menu", LogLevels.UserInput);
			Logger.Log("Press any key (between 0 - 9) for their respective option.\n", LogLevels.UserInput);
			ConsoleKeyInfo key = Console.ReadKey();
			Logger.Log("\n", LogLevels.UserInput);
			if (!int.TryParse(key.KeyChar.ToString(), out int SelectedValue)) {
				Logger.Log("Could not parse the input key. please try again!", LogLevels.Error);
				Logger.Log("Command menu closed.");
				Logger.Log($"Press q to quit in 5 seconds.");
				Logger.Log($"Press e to exit application immediately.");
				Logger.Log($"Press m to display GPIO menu.");
				Logger.Log($"Press i to stop IMAP Idle notifications.");
				Logger.Log($"Press c to display command menu.");
				return;
			}

			GPIOPinConfig PinStatus;
			switch (SelectedValue) {
				case 1:
					PinStatus = Controller.FetchPinStatus(Config.RelayPins[0]);

					if (PinStatus.IsOn) {
						Controller.SetGPIO(Config.RelayPins[0], GpioPinDriveMode.Output, GpioPinValue.High);
						Logger.Log($"Sucessfully set {Config.RelayPins[0]} pin to OFF.", LogLevels.Sucess);
					}
					else {
						Controller.SetGPIO(Config.RelayPins[0], GpioPinDriveMode.Output, GpioPinValue.Low);
						Logger.Log($"Sucessfully set {Config.RelayPins[0]} pin to ON.", LogLevels.Sucess);
					}
					break;
				case 2:
					PinStatus = Controller.FetchPinStatus(Config.RelayPins[1]);

					if (PinStatus.IsOn) {
						Controller.SetGPIO(Config.RelayPins[1], GpioPinDriveMode.Output, GpioPinValue.High);
						Logger.Log($"Sucessfully set {Config.RelayPins[1]} pin to OFF.", LogLevels.Sucess);
					}
					else {
						Controller.SetGPIO(Config.RelayPins[1], GpioPinDriveMode.Output, GpioPinValue.Low);
						Logger.Log($"Sucessfully set {Config.RelayPins[1]} pin to ON.", LogLevels.Sucess);
					}
					break;
				case 3:
					PinStatus = Controller.FetchPinStatus(Config.RelayPins[2]);

					if (PinStatus.IsOn) {
						Controller.SetGPIO(Config.RelayPins[2], GpioPinDriveMode.Output, GpioPinValue.High);
						Logger.Log($"Sucessfully set {Config.RelayPins[2]} pin to OFF.", LogLevels.Sucess);
					}
					else {
						Controller.SetGPIO(Config.RelayPins[2], GpioPinDriveMode.Output, GpioPinValue.Low);
						Logger.Log($"Sucessfully set {Config.RelayPins[2]} pin to ON.", LogLevels.Sucess);
					}
					break;
				case 4:
					PinStatus = Controller.FetchPinStatus(Config.RelayPins[3]);

					if (PinStatus.IsOn) {
						Controller.SetGPIO(Config.RelayPins[3], GpioPinDriveMode.Output, GpioPinValue.High);
						Logger.Log($"Sucessfully set {Config.RelayPins[3]} pin to OFF.", LogLevels.Sucess);
					}
					else {
						Controller.SetGPIO(Config.RelayPins[3], GpioPinDriveMode.Output, GpioPinValue.Low);
						Logger.Log($"Sucessfully set {Config.RelayPins[3]} pin to ON.", LogLevels.Sucess);
					}
					break;
				case 5:
					PinStatus = Controller.FetchPinStatus(Config.RelayPins[4]);

					if (PinStatus.IsOn) {
						Controller.SetGPIO(Config.RelayPins[4], GpioPinDriveMode.Output, GpioPinValue.High);
						Logger.Log($"Sucessfully set {Config.RelayPins[4]} pin to OFF.", LogLevels.Sucess);
					}
					else {
						Controller.SetGPIO(Config.RelayPins[4], GpioPinDriveMode.Output, GpioPinValue.Low);
						Logger.Log($"Sucessfully set {Config.RelayPins[4]} pin to ON.", LogLevels.Sucess);
					}
					break;
				case 6:
					PinStatus = Controller.FetchPinStatus(Config.RelayPins[5]);

					if (PinStatus.IsOn) {
						Controller.SetGPIO(Config.RelayPins[5], GpioPinDriveMode.Output, GpioPinValue.High);
						Logger.Log($"Sucessfully set {Config.RelayPins[5]} pin to OFF.", LogLevels.Sucess);
					}
					else {
						Controller.SetGPIO(Config.RelayPins[5], GpioPinDriveMode.Output, GpioPinValue.Low);
						Logger.Log($"Sucessfully set {Config.RelayPins[5]} pin to ON.", LogLevels.Sucess);
					}
					break;
				case 7:
					PinStatus = Controller.FetchPinStatus(Config.RelayPins[6]);

					if (PinStatus.IsOn) {
						Controller.SetGPIO(Config.RelayPins[6], GpioPinDriveMode.Output, GpioPinValue.High);
						Logger.Log($"Sucessfully set {Config.RelayPins[6]} pin to OFF.", LogLevels.Sucess);
					}
					else {
						Controller.SetGPIO(Config.RelayPins[6], GpioPinDriveMode.Output, GpioPinValue.Low);
						Logger.Log($"Sucessfully set {Config.RelayPins[6]} pin to ON.", LogLevels.Sucess);
					}
					break;
				case 8:
					PinStatus = Controller.FetchPinStatus(Config.RelayPins[7]);

					if (PinStatus.IsOn) {
						Controller.SetGPIO(Config.RelayPins[7], GpioPinDriveMode.Output, GpioPinValue.High);
						Logger.Log($"Sucessfully set {Config.RelayPins[7]} pin to OFF.", LogLevels.Sucess);
					}
					else {
						Controller.SetGPIO(Config.RelayPins[7], GpioPinDriveMode.Output, GpioPinValue.Low);
						Logger.Log($"Sucessfully set {Config.RelayPins[7]} pin to ON.", LogLevels.Sucess);
					}
					break;
				case 9:
					Logger.Log("Please enter the pin u want to configure: ", LogLevels.UserInput);
					string pinNumberKey = Console.ReadLine();
					int pinNumber = 0;
					int delay = 0;
					int pinStatus = 0;

					if (!int.TryParse(pinNumberKey, out pinNumber) || Convert.ToInt32(pinNumberKey) <= 0) {
						Logger.Log("Your entered pin number is incorrect. please enter again.", LogLevels.UserInput);

						pinNumberKey = Console.ReadLine();
						if (!int.TryParse(pinNumberKey, out pinNumber) || Convert.ToInt32(pinNumberKey) <= 0) {
							Logger.Log("Your entered pin number is incorrect again. press m for menu, and start again!", LogLevels.UserInput);
							return;
						}
					}

					Logger.Log("Please enter the amount of delay you want in between the task. (in minutes)", LogLevels.UserInput);
					string delayInMinuteskey = Console.ReadLine();
					if (!int.TryParse(delayInMinuteskey, out delay) || Convert.ToInt32(delayInMinuteskey) <= 0) {
						Logger.Log("Your entered delay is incorrect. please enter again.", LogLevels.UserInput);

						delayInMinuteskey = Console.ReadLine();
						if (!int.TryParse(delayInMinuteskey, out delay) || Convert.ToInt32(delayInMinuteskey) <= 0) {
							Logger.Log("Your entered pin is incorrect again. press m for menu, and start again!", LogLevels.UserInput);
							return;
						}
					}

					Logger.Log("Please enter the status u want the task to configure: (0 = OFF, 1 = ON)", LogLevels.UserInput);

					string pinStatuskey = Console.ReadLine();
					if (!int.TryParse(pinStatuskey, out pinStatus) || (Convert.ToInt32(pinStatuskey) != 0 && Convert.ToInt32(pinStatus) != 1)) {
						Logger.Log("Your entered pin status is incorrect. please enter again.", LogLevels.UserInput);

						pinStatuskey = Console.ReadLine();
						if (!int.TryParse(pinStatuskey, out pinStatus) || (Convert.ToInt32(pinStatuskey) != 0 && Convert.ToInt32(pinStatus) != 1)) {
							Logger.Log("Your entered pin status is incorrect again. press m for menu, and start again!", LogLevels.UserInput);
							return;
						}
					}

					GPIOPinConfig Status = Controller.FetchPinStatus(pinNumber);

					if (Status.IsOn && pinStatus.Equals(1)) {
						Logger.Log("Pin is already configured to be in ON State. Command doesn't make any sense.");
						return;
					}

					if (!Status.IsOn && pinStatus.Equals(0)) {
						Logger.Log("Pin is already configured to be in OFF State. Command doesn't make any sense.");
						return;
					}

					if (Config.IRSensorPins.Contains(pinNumber)) {
						Logger.Log("Sorry, the specified pin is pre-configured for IR Sensor. cannot modify!");
						return;
					}

					if (!Config.RelayPins.Contains(pinNumber)) {
						Logger.Log("Sorry, the specified pin doesn't exist in the relay pin catagory.");
						return;
					}

					Helpers.ScheduleTask(() => {
						if (Status.IsOn && pinStatus.Equals(0)) {
							Controller.SetGPIO(pinNumber, GpioPinDriveMode.Output, GpioPinValue.High);
							Logger.Log($"Sucessfully finished execution of the task: {pinNumber} pin set to OFF.", LogLevels.Sucess);
						}

						if (!Status.IsOn && pinStatus.Equals(1)) {
							Controller.SetGPIO(pinNumber, GpioPinDriveMode.Output, GpioPinValue.Low);
							Logger.Log($"Sucessfully finished execution of the task: {pinNumber} pin set to ON.", LogLevels.Sucess);
						}
					}, TimeSpan.FromMinutes(delay));

					if (pinStatus.Equals(0)) {
						Logger.Log($"Successfully scheduled a task: set {pinNumber} pin to OFF", LogLevels.Sucess);
					}
					else {
						Logger.Log($"Successfully scheduled a task: set {pinNumber} pin to ON", LogLevels.Sucess);
					}
					break;
			}

			Logger.Log("Command menu closed.");
			Logger.Log($"Press q to quit in 5 seconds.");
			Logger.Log($"Press e to exit application immediately.");
			Logger.Log($"Press m to display GPIO menu.");
			Logger.Log($"Press i to stop IMAP Idle notifications.");
			Logger.Log($"Press c to display command menu.");
		}

		public static async Task DisplayRelayMenu() {
			if (DisablePiMethods) {
				return;
			}

			Logger.Log("--------------------MODE MENU--------------------", LogLevels.UserInput);
			Logger.Log("1 | Relay Cycle", LogLevels.UserInput);
			Logger.Log("2 | Relay OneMany", LogLevels.UserInput);
			Logger.Log("3 | Relay OneOne", LogLevels.UserInput);
			Logger.Log("4 | Relay OneTwo", LogLevels.UserInput);
			Logger.Log("5 | Relay Single", LogLevels.UserInput);
			Logger.Log("6 | Relay Default", LogLevels.UserInput);
			Logger.Log("0 | Exit", LogLevels.UserInput);
			Logger.Log("Press any key (between 0 - 6) for their respective option.\n", LogLevels.UserInput);
			ConsoleKeyInfo key = Console.ReadKey();
			Logger.Log("\n", LogLevels.UserInput);

			if (!int.TryParse(key.KeyChar.ToString(), out int SelectedValue)) {
				Logger.Log("Could not parse the input key. please try again!", LogLevels.Error);
				Logger.Log("Press m for menu.", LogLevels.Info);
				return;
			}

			bool Configured;
			switch (SelectedValue) {
				case 1:
					Configured = await Controller.RelayTestService(GPIOCycles.Cycle).ConfigureAwait(false);

					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}

					break;

				case 2:
					Configured = await Controller.RelayTestService(GPIOCycles.OneMany).ConfigureAwait(false);

					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}

					break;

				case 3:
					Configured = await Controller.RelayTestService(GPIOCycles.OneOne).ConfigureAwait(false);
					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}
					break;
				case 4:
					Configured = await Controller.RelayTestService(GPIOCycles.OneTwo).ConfigureAwait(false);

					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}
					break;

				case 5:
					Logger.Log("\nPlease select the channel (2, 3, 4, 17, 27, 22, 10, 9, etc): ", LogLevels.UserInput);
					ConsoleKeyInfo singleKey = Console.ReadKey();
					int selectedsingleKey;

					if (!int.TryParse(singleKey.KeyChar.ToString(), out selectedsingleKey)) {
						Logger.Log("Could not prase the input key. please try again!", LogLevels.Error);
						goto case 5;
					}

					Configured = await Controller.RelayTestService(GPIOCycles.Single, selectedsingleKey).ConfigureAwait(false);

					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}
					break;

				case 6:
					Configured = await Controller.RelayTestService(GPIOCycles.Default).ConfigureAwait(false);

					if (!Configured) {
						Logger.Log("Could not configure the setting. please try again!", LogLevels.Warn);
					}
					break;

				case 0:
					Logger.Log("Exiting from menu...", LogLevels.UserInput);
					return;

				default:
					goto case 0;
			}

			if (Configured) {
				Logger.Log("Test sucessfull!");
			}
			else {
				Logger.Log("Test Failed!");
			}

			Logger.Log("Relay menu closed.");
			Logger.Log($"Press q to quit in 5 seconds.");
			Logger.Log($"Press e to exit application immediately.");
			Logger.Log($"Press m to display GPIO menu.");
			Logger.Log($"Press i to stop IMAP Idle notifications.");
			Logger.Log($"Press c to display command menu.");
		}

		public static void HandleTaskExceptions(object sender, UnobservedTaskExceptionEventArgs e) {
			Logger.Log($"{e.Exception.InnerException}/{e.Exception.Message}/{e.Exception.TargetSite}", LogLevels.Warn);
			Logger.Log($"{e.Exception.ToString()}", LogLevels.Trace);
		}

		public static void HandleFirstChanceExceptions(object sender, FirstChanceExceptionEventArgs e) {
			if (!Config.Debug) {
				return;
			}
			if (e.Exception is PlatformNotSupportedException || e.Exception is OperationCanceledException || e.Exception is SocketException) {
				Logger.Log(e.Exception.ToString(), LogLevels.Trace);
			}
			else {
				//Logger.Log($"{e.Exception.Source}/{e.Exception.Message}/{e.Exception.TargetSite}/{e.Exception.StackTrace}", LogLevels.Warn);
				Logger.Log(e.Exception, ExceptionLogLevels.Fatal);
			}
		}

		public static void HandleUnhandledExceptions(object sender, UnhandledExceptionEventArgs e) {
			Logger.Log($"{e.ToString()}", LogLevels.Trace);

			if (e.IsTerminating) {
				Task.Run(async () => await Exit(1).ConfigureAwait(false));
			}
		}

		public static async Task OnExit() {
			if (Modules != null) {
				await Modules.OnCoreShutdown().ConfigureAwait(false);
			}

			if (Config != null) {
				Config.ProgramLastShutdown = DateTime.Now;
				Config.SaveConfig(Config);
			}

			if (Controller != null) {
				await Controller.InitShutdown().ConfigureAwait(false);
			}

			if (CoreServer != null && CoreServer.ServerOn) {
				await CoreServer.StopServer().ConfigureAwait(false);
			}

			if (RefreshConsoleTitleTimer != null) {
				RefreshConsoleTitleTimer.Dispose();
			}

			if (Update != null) {
				Update.StopUpdateTimer();
			}
		}

		public static async Task Exit(byte exitCode = 0) {
			if (exitCode != 0) {
				Logger.Log("Exiting with nonzero error code...", LogLevels.Error);
				Logger.Log("Check TraceLog for debug information.", LogLevels.Error);
			}

			if (exitCode == 0) {
				await OnExit().ConfigureAwait(false);
			}

			Logger.Log("Shutting down...");
			Logging.LoggerOnShutdown();
			Environment.Exit(exitCode);
		}

		public static async Task Restart(int delay = 10) {
			if (!Config.AutoRestart) {
				Logger.Log("Auto restart is turned off in config.", LogLevels.Warn);
				return;
			}

			Helpers.ScheduleTask(() => Helpers.ExecuteCommand("cd /home/pi/Desktop/HomeAssistant/Helpers/Restarter && dotnet RestartHelper.dll", false), TimeSpan.FromSeconds(delay));
			await Exit(0).ConfigureAwait(false);
		}
	}
}
