namespace VoucherEngine.Domain.Exceptions;

public class VoucherDomainException : Exception
{
    public string ErrorCode { get; }

    public VoucherDomainException(string errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }
}
