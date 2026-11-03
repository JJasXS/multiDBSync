namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// Reports whether the SDK COM password secret is available without exposing its value.
/// </summary>
public static class SqlAccSdkPasswordStatus
{
    /// <summary>
    /// For <c>env:NAME</c> refs, checks Process, then User, then Machine.
    /// Non-env refs are treated as configured at the options layer (resolved later by <c>ISecretResolver</c>).
    /// </summary>
    public static bool IsConfigured(string? passwordSecretRef, out string detail)
    {
        var pwdRef = (passwordSecretRef ?? "").Trim();
        if (pwdRef.Length == 0)
        {
            detail = "SqlAccSdk:PasswordSecretRef is empty.";
            return false;
        }

        if (!pwdRef.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            detail = "PasswordSecretRef is non-env; value resolved at Login time via ISecretResolver.";
            return true;
        }

        var envName = pwdRef["env:".Length..].Trim();
        if (envName.Length == 0)
        {
            detail = "PasswordSecretRef env name is empty.";
            return false;
        }

        if (HasNonEmpty(envName, EnvironmentVariableTarget.Process))
        {
            detail = $"Environment variable {envName} is set (Process).";
            return true;
        }

        if (HasNonEmpty(envName, EnvironmentVariableTarget.User))
        {
            detail = $"Environment variable {envName} is set (User).";
            return true;
        }

        if (HasNonEmpty(envName, EnvironmentVariableTarget.Machine))
        {
            detail = $"Environment variable {envName} is set (Machine).";
            return true;
        }

        detail =
            $"Environment variable {envName} is not set in Process, User, or Machine. " +
            "Add it to the local .env (never commit) or set a User/process env var, then restart the Website.";
        return false;
    }

    /// <summary>
    /// Ensures a User/Machine-scoped env var is visible on the current Process (no value logging).
    /// DotNetEnv / service hosts sometimes only see Process scope.
    /// </summary>
    public static bool TryPromoteEnvPasswordToProcess(string? passwordSecretRef, out string detail)
    {
        detail = "";
        var pwdRef = (passwordSecretRef ?? "").Trim();
        if (!pwdRef.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return false;

        var envName = pwdRef["env:".Length..].Trim();
        if (envName.Length == 0)
            return false;

        if (HasNonEmpty(envName, EnvironmentVariableTarget.Process))
        {
            detail = "Already present on Process.";
            return false;
        }

        var user = Environment.GetEnvironmentVariable(envName, EnvironmentVariableTarget.User);
        if (!string.IsNullOrEmpty(user))
        {
            Environment.SetEnvironmentVariable(envName, user, EnvironmentVariableTarget.Process);
            detail = "Promoted User → Process (value not logged).";
            return true;
        }

        var machine = Environment.GetEnvironmentVariable(envName, EnvironmentVariableTarget.Machine);
        if (!string.IsNullOrEmpty(machine))
        {
            Environment.SetEnvironmentVariable(envName, machine, EnvironmentVariableTarget.Process);
            detail = "Promoted Machine → Process (value not logged).";
            return true;
        }

        detail = "No User/Machine value to promote.";
        return false;
    }

    private static bool HasNonEmpty(string name, EnvironmentVariableTarget target) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name, target));
}
