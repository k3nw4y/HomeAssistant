namespace Assistant.Server.TCPServer.Events {
	public class ClientMessageEventArgs {
		public string RawCommandJson { get; set; } = string.Empty;

		public ClientMessageEventArgs(string command) => RawCommandJson = command;
	}
}
