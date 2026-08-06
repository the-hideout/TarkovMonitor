namespace TarkovMonitor
{
    internal static class TarkovTrackerOrgTokenVerifier
    {
        public static string GetVerifiedPrefix(
            string apiToken,
            string? returnedToken,
            string? gameMode)
        {
            var suppliedPrefix = TarkovTracker.GetTokenPrefix(apiToken);
            if (!TarkovTracker.IsImportablePrefix(suppliedPrefix))
            {
                throw new Exception("The verified API key must be a PVP_, PVE_, or SZN_ key. For accuracy, copy the API key directly from TarkovTracker.org instead of typing or editing it manually.");
            }

            if (!string.IsNullOrWhiteSpace(returnedToken)
                && !ApiKeyIdentityMatches(apiToken, returnedToken))
            {
                throw new Exception("The API key returned by TarkovTracker does not exactly match the imported key. For accuracy, copy it directly from TarkovTracker.org instead of typing or editing it manually.");
            }

            var verifiedPrefix = gameMode?.Trim().ToLowerInvariant() switch
            {
                "pvp" => "PVP",
                "pve" => "PVE",
                "seasonal" => "SZN",
                _ => throw new Exception("TarkovTracker did not return a supported PVP, PVE, or Seasonal game mode for this API key."),
            };
            if (!string.Equals(suppliedPrefix, verifiedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception($"This key is marked {TarkovTracker.GetPrefixDisplayName(suppliedPrefix)}, but TarkovTracker verified it as {TarkovTracker.GetPrefixDisplayName(verifiedPrefix)}. For accuracy, copy the API key directly from TarkovTracker.org instead of typing or editing it manually.");
            }
            return verifiedPrefix;
        }

        private static bool ApiKeyIdentityMatches(string suppliedToken, string returnedToken)
        {
            var suppliedSeparator = suppliedToken.IndexOf('_');
            var returnedSeparator = returnedToken.IndexOf('_');
            if (suppliedSeparator <= 0 || returnedSeparator <= 0)
            {
                return false;
            }

            var suppliedPrefix = suppliedToken[..suppliedSeparator];
            var returnedPrefix = returnedToken[..returnedSeparator];
            var suppliedIdentifier = suppliedToken[(suppliedSeparator + 1)..];
            var returnedIdentifier = returnedToken[(returnedSeparator + 1)..];
            return string.Equals(suppliedPrefix, returnedPrefix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(suppliedIdentifier, returnedIdentifier, StringComparison.Ordinal);
        }
    }
}
