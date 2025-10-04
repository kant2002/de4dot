namespace de4dot.mcp {
	public class DeobfuscationSessionManager {
		private Dictionary<string, DeobfuscationSession> sessions = new();
		public DeobfuscationSession GetSession(string name) {
			if (sessions.ContainsKey(name)) { return sessions[name]; }
			sessions[name] = new DeobfuscationSession();
			return sessions[name];
		}
	}
}
