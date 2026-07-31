namespace de4dot.code {
    /// <summary>
    /// Identifies a string decrypter method by its metadata token.
    /// </summary>
    public sealed class StringDecrypterMethodInfo {
        public StringDecrypterMethodInfo(int methodToken) {
            MethodToken = methodToken;
        }

        public int MethodToken { get; }
    }
}
