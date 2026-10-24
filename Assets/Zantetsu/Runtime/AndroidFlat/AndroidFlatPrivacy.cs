namespace Zantetsu.AndroidFlat
{
    /// <summary>The bundled policy can be read without a network connection.</summary>
    public static class AndroidFlatPrivacy
    {
        public static readonly string[] Pages = {
            "WORLD CUTTER for Android\n\nPlay statistics are optional. You can play without agreeing. Nothing is collected or sent before agreement or while statistics are OFF.\n\nWe use the statistics to understand performance and stability. They are sent to our Cloudflare Worker over HTTPS and stored in Cloudflare R2. They are not used for advertising.",
            "What is sent\n\nApp version; OS, device model, CPU, GPU, graphics API and driver; display size, scene, graphics and control settings; aggregate play time and frame times; totals of cuts, fragments and internal failures; buffer usage; random install and session IDs; tester role.\n\nWe do not include your name, email, user-assigned device name, hardware IDs, touch positions, movement paths or images. Individual frame and input histories are not kept.",
            "Retention and your choice\n\nEach stored record becomes eligible for automatic deletion after 90 days; deletion itself may take additional time.\n\nUse Menu > Statistics to withdraw at any time. Collection and sending stop, and the local statistics install ID is removed. Already sent records remain until deleted under the retention rule. Agreeing again creates a new random install ID.\n\nStatistics are normally sent when the app enters the background or loses focus. A forced stop or crash may prevent sending.",
            "Network handling and operational logs\n\nCloudflare processes connection details, including the source IP. The receiver uses an IP-derived hash for short-lived rate limiting. It does not put raw IP addresses in play-statistics records or the rate-limit store.\n\nCloudflare separately stores operational invocation and error logs, such as request method, URL and response status. Under the current service limits, these logs are kept for up to 7 days. We do not deliberately log request bodies or telemetry tokens.",
            "Publisher and contact\n\nPublisher: hogehogeo\n\nPublic support:\nhttps://github.com/piyohogeo/zantetsuken-vr/issues\n\nIssues require a GitHub account and are public. Do not post personal details, access tokens, install/session IDs or unreviewed logs.\n\nEffective date: the first public release."
        };
    }
}
