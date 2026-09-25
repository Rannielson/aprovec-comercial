using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Security;

public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    public static void Validate(string? password)
    {
        if (password is null || password.Length < MinLength || password.Length > MaxLength)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "auth.weak_password");
    }
}
