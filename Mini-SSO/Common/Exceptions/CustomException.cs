namespace Mini_SSO.Common.Exceptions
{
    public sealed class DomainNotFoundException(string entity, object key)
        : Exception($"{entity} with key '{key}' was not found.")
    {
        public string Entity { get; } = entity;
        public object Key { get; } = key;
    }

    public sealed class DomainValidationException : Exception
    {
        public IReadOnlyDictionary<string, string[]> Errors { get; }

        public DomainValidationException(string message)
            : base(message)
        {
            Errors = new Dictionary<string, string[]>();
        }

        public DomainValidationException(IReadOnlyDictionary<string, string[]> errors)
            : base("One or more validation errors occurred.")
        {
            Errors = errors;
        }
    }

    public sealed class TooManyRequestsException(string message, int retryAfterSeconds)
        : Exception(message)
    {
        public int RetryAfterSeconds { get; } = retryAfterSeconds;
    }
}
