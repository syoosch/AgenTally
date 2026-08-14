using System.Text;
using System.Text.Json;
using AgenTally.Domain.Usage;

namespace AgenTally.Core.Collectors.WorkBuddy;

internal static class WorkBuddyModelIdentityResolver
{
    private const int MaxModelCharacters = 256;

    public static ModelIdentity Resolve(JsonElement root)
    {
        JsonElement providerData = ObjectProperty(root, "providerData");
        JsonElement message = ObjectProperty(root, "message");
        string? providerModel = Read(providerData, "model");
        string? routeModelId = Read(providerData, "requestModelId");
        string? displayName = Read(providerData, "requestModelName");
        string? messageModel = Read(message, "model");
        string? rawModel = providerModel ?? messageModel ?? routeModelId ?? displayName;
        string? normalizedRaw = NormalizeIdentifier(rawModel);
        string? normalizedModel = normalizedRaw;
        ModelResolutionOrigin origin = normalizedRaw is null
            ? ModelResolutionOrigin.Unknown
            : ModelResolutionOrigin.LogConfirmed;

        if (normalizedRaw is not null &&
            TryResolveConfirmedDisplayAlias(
                displayName,
                routeModelId,
                out string? alias) &&
            IsAliasConfirmed(normalizedRaw, routeModelId, alias!))
        {
            normalizedModel = alias;
            if (!string.Equals(
                    normalizedRaw,
                    normalizedModel,
                    StringComparison.Ordinal))
            {
                origin = ModelResolutionOrigin.ExactAlias;
            }
        }

        string? providerId = NormalizeIdentifier(
            Read(providerData, "providerId") ??
            Read(providerData, "provider"));
        if (providerId is not null &&
            origin is ModelResolutionOrigin.LogConfirmed)
        {
            origin = ModelResolutionOrigin.ProviderModelPair;
        }

        normalizedModel = ModelIdentityCanonicalizer.Canonicalize(
            normalizedModel,
            "workbuddy",
            providerId);

        return new ModelIdentity
        {
            RawModel = rawModel,
            NormalizedModel = normalizedModel,
            RouteModelId = routeModelId,
            DisplayName = displayName,
            ProviderId = providerId,
            ResolutionOrigin = origin
        };
    }

    private static bool TryResolveConfirmedDisplayAlias(
        string? displayName,
        string? routeModelId,
        out string? normalizedModel)
    {
        normalizedModel = NormalizeIdentifier(displayName);
        string? normalizedRoute = NormalizeIdentifier(routeModelId);
        if (normalizedModel is not null &&
            normalizedRoute is not null &&
            string.Equals(
                NormalizeAliasKey(normalizedModel),
                NormalizeAliasKey(normalizedRoute),
                StringComparison.Ordinal))
        {
            normalizedModel = normalizedRoute;
        }

        return normalizedModel is not null;
    }

    private static bool IsAliasConfirmed(
        string normalizedRaw,
        string? routeModelId,
        string alias)
    {
        if (string.Equals(normalizedRaw, alias, StringComparison.Ordinal))
        {
            return true;
        }

        string? normalizedRoute = NormalizeIdentifier(routeModelId);
        if (string.Equals(normalizedRoute, alias, StringComparison.Ordinal))
        {
            return true;
        }

        string? rawKey = NormalizeAliasKey(normalizedRaw);
        string? aliasKey = NormalizeAliasKey(alias);
        if (rawKey is not null &&
            string.Equals(rawKey, aliasKey, StringComparison.Ordinal))
        {
            return true;
        }

        string? routeKey = NormalizeAliasKey(normalizedRoute);
        if (routeKey is not null &&
            string.Equals(routeKey, aliasKey, StringComparison.Ordinal))
        {
            return true;
        }

        return TryResolveRouteAlias(normalizedRaw, alias) ||
               TryResolveRouteAlias(normalizedRoute, alias);
    }

    private static bool TryResolveRouteAlias(
        string? route,
        string expectedAlias) =>
        route is not null &&
        ModelIdentityCanonicalizer.TryResolveReviewedSourceAlias(
            "workbuddy",
            route,
            out string? alias) &&
        string.Equals(alias, expectedAlias, StringComparison.Ordinal);

    private static string? NormalizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value.Trim().ToLowerInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string? NormalizeAliasKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? Read(JsonElement element, string propertyName) =>
        element.ValueKind is JsonValueKind.Object
            ? WorkBuddyTextNormalizer.ReadBoundedString(
                element,
                propertyName,
                MaxModelCharacters)
            : null;

    private static JsonElement ObjectProperty(
        JsonElement element,
        string propertyName) =>
        element.ValueKind is JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind is JsonValueKind.Object
            ? value
            : default;
}
