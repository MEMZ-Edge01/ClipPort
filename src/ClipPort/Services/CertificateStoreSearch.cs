namespace ClipPort.Services;

internal static class CertificateStoreSearch
{
    public static List<TTarget> FindMatches<TTarget>(
        IEnumerable<TTarget> targets,
        Func<TTarget, bool> containsCertificate)
    {
        var matches = new List<TTarget>();
        foreach (TTarget target in targets)
        {
            // Access failures must remain visible to callers so removal cannot
            // report success when a certificate store could not be inspected.
            if (containsCertificate(target))
            {
                matches.Add(target);
            }
        }

        return matches;
    }
}
