namespace QTFastStart
{
    [Serializable]
    internal class MalformedFileException : Exception
    {
        public MalformedFileException(string? message) : base(message)
        {
        }

        public MalformedFileException(string? message, Exception? innerException) : base(message, innerException)
        {
        }
    }
}