namespace FieldOps.Features.Abstractions;

public interface ICurrentUser
{
    string UserId { get; }

    string Role { get; }
}