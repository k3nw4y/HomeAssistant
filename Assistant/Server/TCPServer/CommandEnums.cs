namespace Assistant.Server.TCPServer {
	public class CommandEnums {
		public enum CommandType {
			InvalidType = 0,
			Gpio = 1,
			Remainder = 2,
			Alarm = 3,
			Weather = 4,
			Client = 5
		}

		public enum ResponseObjectType {
			SetGpio,
			GetGpioAll,
			SetGpioDelayed,
			SetRemainder,
			SetAlarm,
			GetGpio,
			GetWeather,
			Invalid,
			NoResponse
		}

		public enum Command {
			InvalidCommand = 0,
			SetGpioGeneral = 1,
			SetGpioDelayed = 2,
			GetGpio = 3,			
			GetWeather = 4,
			SetRemainder = 5,
			SetAlarm = 6,
			Disconnect = 7,
			Initiate = 8,
			GetOutputPins = 9,
			GetInputPins = 10
		}

		public enum CommandResponseCode {
			OK,
			FAIL,
			INVALID,
			FATAL
		}
	}
}
