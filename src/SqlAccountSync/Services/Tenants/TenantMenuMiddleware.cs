namespace SqlAccountSync.Services.Tenants;

public sealed class TenantMenuMiddleware
{
    private readonly RequestDelegate _next;

    public TenantMenuMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context, TenantMenuFlags flags)
    {
        var path = context.Request.Path.Value ?? "";
        if (BlocksSynchronization(path, flags) || BlocksDocuments(path, flags))
        {
            context.Response.Redirect("/");
            return Task.CompletedTask;
        }

        return _next(context);
    }

    private static bool BlocksSynchronization(string path, TenantMenuFlags flags) =>
        !flags.ShowSynchronization && path.StartsWith("/push", StringComparison.OrdinalIgnoreCase);

    private static bool BlocksDocuments(string path, TenantMenuFlags flags) =>
        !flags.ShowDocumentTransfer
        && (path.StartsWith("/Transactions", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/transactions", StringComparison.OrdinalIgnoreCase));
}
