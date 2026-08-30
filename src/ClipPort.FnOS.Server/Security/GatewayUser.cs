namespace ClipPort.FnOS.Security;

public sealed record GatewayUser(int UserId, string Username, bool IsAdmin)
{
    public const string ItemKey = "ClipPort.GatewayUser";

    public static GatewayUser From(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out object? value) && value is GatewayUser user
            ? user
            : throw new InvalidOperationException("Gateway user context is missing.");
}
