namespace BiStudent.Server.Firebase;

public sealed class FirebaseUnavailableException : Exception
{
    public FirebaseUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public sealed class FirebaseAuthException : Exception
{
    public string Code { get; }

    public bool IsBadCredentials => Code is "INVALID_LOGIN_CREDENTIALS" or
        "INVALID_PASSWORD" or "EMAIL_NOT_FOUND";

    public bool IsUnknownAccount => Code == "EMAIL_NOT_FOUND";

    public FirebaseAuthException(string code) : base(Describe(code))
    {
        Code = code;
    }

    private static string Describe(string code) => code switch
    {
        "INVALID_LOGIN_CREDENTIALS" or "INVALID_PASSWORD" or "EMAIL_NOT_FOUND" =>
            "Неверный логин или пароль.",
        "EMAIL_EXISTS" => "Такая учётная запись уже существует.",
        "WEAK_PASSWORD" => "Пароль слишком слабый.",
        "USER_DISABLED" => "Учётная запись отключена.",
        "TOO_MANY_ATTEMPTS_TRY_LATER" => "Слишком много попыток. Повторите позже.",
        "OPERATION_NOT_ALLOWED" => "Вход по паролю не включён.",
        "CONFIGURATION_NOT_FOUND" => "Firebase не настроен.",
        "TOKEN_EXPIRED" or "INVALID_REFRESH_TOKEN" => "Срок действия входа истёк.",
        "USER_NOT_FOUND" => "Учётная запись не найдена.",
        _ => "Ошибка Firebase Authentication: " + code
    };
}

public sealed class FirestorePermissionException : Exception
{
    public FirestorePermissionException(string message) : base(message) { }
}

public sealed class FirestoreRequestException : Exception
{
    public string Status { get; }

    public FirestoreRequestException(string status, string message) : base(message)
    {
        Status = status;
    }
}
