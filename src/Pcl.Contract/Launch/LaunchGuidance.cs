namespace Pcl.Core.Contracts.Launch;
// Acknowledgements describe a displayed notice, never an OAuth token or proof of ownership.
public sealed record LaunchGuidanceRequest(string AccountId, string Culture);
public sealed record LaunchGuidanceConsent(string Culture, bool SponsorRead, bool PurchaseRead, bool Demo = false);
public sealed record LaunchGuidance(bool ShowSponsor, bool ShowPurchase, bool RequireDemo, string SponsorUrl, string PurchaseUrl);
