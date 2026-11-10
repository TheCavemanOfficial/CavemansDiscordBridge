using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;

namespace CaveheimDiscordBridge
{
    /// <summary>
    /// Discord's certificate, checked without trusting the game's certificate store alone. On Linux servers Mono's
    /// store sometimes breaks for a whole session (every handshake fails with CERTIFICATE_VERIFY_FAILED until the
    /// process restarts). When the normal check fails, and only for Discord's own hosts, the chain is checked again
    /// against the root certificates carried here. The hostname check is never skipped. Compiled into the plugin and the preloader.
    /// </summary>
    internal static class DiscordTls
    {
        /// <summary>Where messages go: the BepInEx log in the plugin, the console in the preloader.</summary>
        internal static Action<string> Log = System.Console.WriteLine;

        /// <summary>Set CAVEMANS_BRIDGE_TLS_TEST=1 to treat every Discord handshake as if the store had failed, to test the fallback.</summary>
        private static readonly bool s_forceFallback = Environment.GetEnvironmentVariable("CAVEMANS_BRIDGE_TLS_TEST") == "1";

        private static int s_fallbackNoted, s_failureNoted, s_installed;

        // Google Trust Services (Discord's CA, via Cloudflare) and Let's Encrypt (Cloudflare's other CA), from pki.goog and
        // letsencrypt.org. Checked on 2026-11-10 against the chains discord.com and gateway.discord.gg serve (WE1 -> GTS Root R4).
        private static readonly string[] s_rootPems =
        {
            // GTS Root R1, expires 2036-06-22
            @"MIIFVzCCAz+gAwIBAgINAgPlk28xsBNJiGuiFzANBgkqhkiG9w0BAQwFADBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjEwHhcNMTYwNjIyMDAwMDAwWhcNMzYwNjIyMDAwMDAwWjBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjEwggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQC2EQKLHuOhd5s73L+UPreVp0A8of2C+X0yBoJx9vaMf/vo27xqLpeXo4xL+Sv2sfnOhB2x+cWX3u+58qPpvBKJXqeqUqv4IyfLpLGcY9vXmX7wCl7raKb0xlpHDU0QM+NOsROjyBhsS+z8CZDfnWQpJSMHobTSPS5g4M/SCYe7zUjwTcLCeoiKu7rPWRnWr4+wB7CeMfGCwcDfLqZtbBkOtdh+JhpFAz2weaSUKK0PfyblqAj+lug8aJRT7oM6iCsVlgmy4HqMLnXWnOunVmSPlk9orj2XwoSPwLxAwAtcvfaHszVsrBhQf4TgTM2S0yDpM7xSma8ytSmzJSq0SPly4cpk9+aCEI3oncKKiPo4Zor8Y/kB+Xj9e1x3+naH+uzfsQ55lVe0vSbv1gHR6xYKu44LtcXFilWr06zqkUspzBmkMiVOKvFlRNACzqrOSbTqn3yDsEB750Orp2yjj32JgfpMpf/VjsPOS+C12LOORc92wO1AK/1TD7Cn1TsNsYqiA94xrcx36m97PtbfkSIS5r762DL8EGMUUXLeXdYWk70paDPvOmbsB4om3xPXV2V4J95eSRQAogB/mqghtqmxlbCluQ0WEdrHbEg8QOB+DVrNVjzRlwW5y0vtOUucxD/SVRNuJLDWcfr0wbrM7Rv1/oFB2ACYPTrIrnqYNxgFlQIDAQABo0IwQDAOBgNVHQ8BAf8EBAMCAYYwDwYDVR0TAQH/BAUwAwEB/zAdBgNVHQ4EFgQU5K8rJnEaK0gnhS9SZizv8IkTcT4wDQYJKoZIhvcNAQEMBQADggIBAJ+qQibbC5u+/x6Wki4+omVKapi6Ist9wTrYggoGxval3sBOh2Z5ofmmWJyq+bXmYOfg6LEeQkEzCzc9zolwFcq1JKjPa7XSQCGYzyI0zzvFIoTgxQ6KfF2I5DUkzps+GlQebtuyh6f88/qBVRRiClmpIgUxPoLW7ttXNLwzldMXG+gnoot7TiYaelpkttGsN/H9oPM47HLwEXWdyzRSjeZ2axfG34arJ45JK3VmgRAhpuo+9K4l/3wV3s6MJT/KYnAK9y8JZgfIPxz88NtFMN9iiMG1D53Dn0reWVlHxYciNuaCp+0KueIHoI17eko8cdLiA6EfMgfdG+RCzgwARWGAtQsgWSl4vflVy2PFPEz0tv/bal8xa5meLMFrUKTX5hgUvYU/Z6tGn6D/Qqc6f1zLXbBwHSs09dR2CQzreExZBfMzQsNhFRAbd03OIozUhfJFfbdT6u9AWpQKXCBfTkBdYiJ23//OYb2MI3jSNwLgjt7RETeJ9r/tSQdirpLsQBqvFAnZ0E6yove+7u7Y/9waLd64NnHi/Hm3lCXRSHNboTXns5lndcEZOitHTtNCjv0xyBZm2tIMPNuzjsmhDYAPexZ3FL//2wmUspO8IFgV6dtxQ/PeEMMA3KgqlbbC1j+Qa3bbbP6MvPJwNQzcmRk13NfIRmPVNnGuV/u3gm3c",
            // GTS Root R2, expires 2036-06-22
            @"MIIFVzCCAz+gAwIBAgINAgPlrsWNBCUaqxElqjANBgkqhkiG9w0BAQwFADBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjIwHhcNMTYwNjIyMDAwMDAwWhcNMzYwNjIyMDAwMDAwWjBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjIwggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQDO3v2m++zsFDQ8BwZabFn3GTXd98GdVarTzTukk3LvCvptnfbwhYBboUhSnznFt+4orO/LdmgUud+tAWyZH8QiHZ/+cnfgLFuv5AS/T3KgGjSY6Dlo7JUle3ah5mm5hRm9iYz+re026nO8/4Piy33B0s5Ks40FnotJk9/BW9BuXvAuMC6C/Pq8tBcKSOWIm8Wba96wyrQD8Nr0kLhlZPdcTK3ofmZemde4wj7I0BOdre7kRXuJVfeKH2JShBKzwkCX44ofR5GmdFrS+LFjKBC4swm4VndAoiaYecb+3yXuPuWgf9RhD1FLPD+M2uFwdNjCaKH5wQzpoeJ/u1U8dgbuak7MkogwTZq9TwtImoS1mKPV+3PBV2HdKFZ1E66HjucMUQkQdYhMvI35ezzUIkgfKtzra7tEscszcTJGr61K8YzodDqs5xoic4DSMPclQsciOzsSrZYuxsN2B6ogtzVJV+mSSeh2FnIxZyuWfoqjx5RWIr9qS34BIbIjMt/kmkRtWVtd9QCgHJvGeJeNkP+byKq0rxFROV7Z+2et1VsRnTKaG73VululycslaVNVJ1zgyjbLiGH7HrfQy+4W+9OmTN6SpdTi3/UGVN4unUu0kzCqgc7dGtxRcw1PcOnlthYhGXmy5okLdWTK1au8CcEYof/UVKGFPP0UJAOyh9OktwIDAQABo0IwQDAOBgNVHQ8BAf8EBAMCAYYwDwYDVR0TAQH/BAUwAwEB/zAdBgNVHQ4EFgQUu//KjiOfT5nK2+JopqUVJxce2Q4wDQYJKoZIhvcNAQEMBQADggIBAB/Kzt3HvqGf2SdMC9wXmBFqiN495nFWcrKeGk6c1SuYJF2ba3uwM4IJvd8lRuqYnrYb/oM80mJhwQTtzuDFycgTE1XnqGOtjHsB/ncw4c5omwX4Eu55MaBBRTUoCnGkJE+M3DyCB19m3H0Q/gxhswWV7uGugQ+o+MePTagjAiZrHYNSVc61LwDKgEDg4XSsYPWHgJ2uNmSRXbBoGOqKYcl3qJfEycel/FVL8/B/uWU9J2jQzGv6U53hkRrJXRqWbTKH7QMgyALOWr7Z6v2yTcQvG99fevX4i8buMTolUVVnjWQye+mew4K6Ki3pHrTgSAai/GevHyICc/sgCq+dVEuhzf9gR7A/Xe8bVr2XIZYtCtFenTgCR2y59PYjJbigapordwj6xLEokCZYCDzifqrXPW+6MYgKBesntaFJ7qBFVHvmJ2WZICGoo7z7GJa7Um8M7YNRTOlZ4iBgxcJlkoKM8xAfDoqXvneCbT+PHV28SSe9zE8P4c52hgQjxcCMElv924SgJPFI/2R80L5cFtHvma3AH/vLrrw4IgYmZNralw4/KBVEqE8AyvCazM90arQ+POuV7LXTWtiBmelDGDfrs7vRWGJB82bSj6p4lVQgw1oudCvV0b4YacCs1aTPObpRhANl6WLAYv7YTVWW4tAR+kg0Eeye7QUd5MjWHYbL",
            // GTS Root R3, expires 2036-06-22
            @"MIICCTCCAY6gAwIBAgINAgPluILrIPglJ209ZjAKBggqhkjOPQQDAzBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjMwHhcNMTYwNjIyMDAwMDAwWhcNMzYwNjIyMDAwMDAwWjBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjMwdjAQBgcqhkjOPQIBBgUrgQQAIgNiAAQfTzOHMymKoYTey8chWEGJ6ladK0uFxh1MJ7x/JlFyb+Kf1qPKzEUURout736GjOyxfi//qXGdGIRFBEFVbivqJn+7kAHjSxm65FSWRQmx1WyRRK2EE46ajA2ADDL24CejQjBAMA4GA1UdDwEB/wQEAwIBhjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBTB8Sa6oC2uhYHP0/EqEr24Cmf9vDAKBggqhkjOPQQDAwNpADBmAjEA9uEglRR7VKOQFhG/hMjqb2sXnh5GmCCbn9MN2azTL818+FsuVbu/3ZL3pAzcMeGiAjEA/JdmZuVDFhOD3cffL74UOO0BzrEXGhF16b0DjyZ+hOXJYKaV11RZt+cRLInUue4X",
            // GTS Root R4, expires 2036-06-22 (discord.com and gateway.discord.gg chain to this one)
            @"MIICCTCCAY6gAwIBAgINAgPlwGjvYxqccpBQUjAKBggqhkjOPQQDAzBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjQwHhcNMTYwNjIyMDAwMDAwWhcNMzYwNjIyMDAwMDAwWjBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjQwdjAQBgcqhkjOPQIBBgUrgQQAIgNiAATzdHOnaItgrkO4NcWBMHtLSZ37wWHO5t5GvWvVYRg1rkDdc/eJkTBa6zzuhXyiQHY7qca4R9gq55KRanPpsXI5nymfopjTX15YhmUPoYRlBtHci8nHc8iMai/lxKvRHYqjQjBAMA4GA1UdDwEB/wQEAwIBhjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBSATNbrdP9JNqPV2Py1PsVq8JQdjDAKBggqhkjOPQQDAwNpADBmAjEA6ED/g94D9J+uHXqnLrmvT/aDHQ4thQEd0dlq7A/Cr8deVl5c1RxYIigL9zC2L7F8AjEA8GE8p/SgguMh1YQdc4acLa/KNJvxn7kjNuK8YAOdgLOaVsjh4rsUecrNIdSUtUlD",
            // ISRG Root X1, expires 2035-06-04
            @"MIIFazCCA1OgAwIBAgIRAIIQz7DSQONZRGPgu2OCiwAwDQYJKoZIhvcNAQELBQAwTzELMAkGA1UEBhMCVVMxKTAnBgNVBAoTIEludGVybmV0IFNlY3VyaXR5IFJlc2VhcmNoIEdyb3VwMRUwEwYDVQQDEwxJU1JHIFJvb3QgWDEwHhcNMTUwNjA0MTEwNDM4WhcNMzUwNjA0MTEwNDM4WjBPMQswCQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJuZXQgU2VjdXJpdHkgUmVzZWFyY2ggR3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBYMTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAK3oJHP0FDfzm54rVygch77ct984kIxuPOZXoHj3dcKi/vVqbvYATyjb3miGbESTtrFj/RQSa78f0uoxmyF+0TM8ukj13Xnfs7j/EvEhmkvBioZxaUpmZmyPfjxwv60pIgbz5MDmgK7iS4+3mX6UA5/TR5d8mUgjU+g4rk8Kb4Mu0UlXjIB0ttov0DiNewNwIRt18jA8+o+u3dpjq+sWT8KOEUt+zwvo/7V3LvSye0rgTBIlDHCNAymg4VMk7BPZ7hm/ELNKjD+Jo2FR3qyHB5T0Y3HsLuJvW5iB4YlcNHlsdu87kGJ55tukmi8mxdAQ4Q7e2RCOFvu396j3x+UCB5iPNgiV5+I3lg02dZ77DnKxHZu8A/lJBdiB3QW0KtZB6awBdpUKD9jf1b0SHzUvKBds0pjBqAlkd25HN7rOrFleaJ1/ctaJxQZBKT5ZPt0m9STJEadao0xAH0ahmbWnOlFuhjuefXKnEgV4We0+UXgVCwOPjdAvBbI+e0ocS3MFEvzG6uBQE3xDk3SzynTnjh8BCNAw1FtxNrQHusEwMFxIt4I7mKZ9YIqioymCzLq9gwQbooMDQaHWBfEbwrbwqHyGO0aoSCqI3Haadr8faqU9GY/rOPNk3sgrDQoo//fb4hVC1CLQJ13hef4Y53CIrU7m2Ys6xt0nUW7/vGT1M0NPAgMBAAGjQjBAMA4GA1UdDwEB/wQEAwIBBjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBR5tFnme7bl5AFzgAiIyBpY9umbbjANBgkqhkiG9w0BAQsFAAOCAgEAVR9YqbyyqFDQDLHYGmkgJykIrGF1XIpu+ILlaS/V9lZLubhzEFnTIZd+50xx+7LSYK05qAvqFyFWhfFQDlnrzuBZ6brJFe+GnY+EgPbk6ZGQ3BebYhtF8GaV0nxvwuo77x/Py9auJ/GpsMiu/X1+mvoiBOv/2X/qkSsisRcOj/KKNFtY2PwByVS5uCbMiogziUwthDyC3+6WVwW6LLv3xLfHTjuCvjHIInNzktHCgKQ5ORAzI4JMPJ+GslWYHb4phowim57iaztXOoJwTdwJx4nLCgdNbOhdjsnvzqvHu7UrTkXWStAmzOVyyghqpZXjFaH3pO3JLF+l+/+sKAIuvtd7u+Nxe5AW0wdeRlN8NwdCjNPElpzVmbUq4JUagEiuTDkHzsxHpFKVK7q4+63SM1N95R1NbdWhscdCb+ZAJzVcoyi3B43njTOQ5yOf+1CceWxG1bQVs5ZufpsMljq4Ui0/1lvh+wjChP4kqKOJ2qxq4RgqsahDYVvTH9w7jXbyLeiNdd8XM2w9U/t7y0Ff/9yi0GE44Za4rF2LN9d11TPAmRGunUHBcnWEvgJBQl9nJEiU0Zsnvgc/ubhPgXRR4Xq37Z0j4r7g1SgEEzwxA57demyPxgcYxn/eR44/KJ4EBs+lVDR3veyJm+kXQ99b21/+jh5Xos1AnX5iItreGCc=",
            // ISRG Root X2, expires 2040-09-17
            @"MIICGzCCAaGgAwIBAgIQQdKd0XLq7qeAwSxs6S+HUjAKBggqhkjOPQQDAzBPMQswCQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJuZXQgU2VjdXJpdHkgUmVzZWFyY2ggR3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBYMjAeFw0yMDA5MDQwMDAwMDBaFw00MDA5MTcxNjAwMDBaME8xCzAJBgNVBAYTAlVTMSkwJwYDVQQKEyBJbnRlcm5ldCBTZWN1cml0eSBSZXNlYXJjaCBHcm91cDEVMBMGA1UEAxMMSVNSRyBSb290IFgyMHYwEAYHKoZIzj0CAQYFK4EEACIDYgAEzZvVn4CDCuwJSvMWSj5cz3es3mcFDR0HttwW+1qLFNvicWDEukWVEYmO6gbf9yoWHKS5xcUy4APgHoIYOIvXRdgKam7mAHf7AlF9ItgKbppbd9/w+kHsOdx1ymgHDB/qo0IwQDAOBgNVHQ8BAf8EBAMCAQYwDwYDVR0TAQH/BAUwAwEB/zAdBgNVHQ4EFgQUfEKWrt5LSDv6kviejM9ti6lyN5UwCgYIKoZIzj0EAwMDaAAwZQIwe3lORlCEwkSHRhtFcP9Ymd70/aTSVaYgLXTWNLxBo1BfASdWtL4ndQavEi51mI38AjEAi/V3bNTIZargCyzuFJ0nN6T5U6VR5CmD1/iQMVtCnwr1/q4AaOeMSQ+2b1tbFfLn",
        };

        private static readonly X509Certificate2[] s_roots = LoadRoots();

        private static X509Certificate2[] LoadRoots()
        {
            var list = new List<X509Certificate2>();
            foreach (string pem in s_rootPems)
            {
                try { list.Add(new X509Certificate2(Convert.FromBase64String(pem))); }
                catch (Exception ex) { Log($"[Caveman's Discord Bridge] Couldn't load a built-in root certificate: {ex.Message}"); }
            }
            return list.ToArray();
        }

        /// <summary>An HttpClient whose Discord handshakes fall back to the built-in roots.</summary>
        internal static HttpClient NewClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler();
            try { handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) => Validate(request?.RequestUri?.Host, cert, chain, errors); }
            catch (Exception ex) { Log($"[Caveman's Discord Bridge] This runtime can't take a certificate check ({ex.Message}); using the default one."); }
            return new HttpClient(handler) { Timeout = timeout };
        }

        /// <summary>
        /// For connections that don't go through HttpClient (Mono's ClientWebSocket uses HttpWebRequest). Chains to any
        /// check another mod installed: hosts that aren't Discord's get exactly the answer they got before.
        /// </summary>
        internal static void InstallGlobal()
        {
            if (Interlocked.Exchange(ref s_installed, 1) == 1) return;
            RemoteCertificateValidationCallback previous = ServicePointManager.ServerCertificateValidationCallback;
            ServicePointManager.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                string host = (sender as HttpWebRequest)?.RequestUri?.Host ?? (sender as string);
                bool discord = host == null || IsDiscordHost(host);
                if (!discord || errors == SslPolicyErrors.None && !s_forceFallback)
                    return previous != null ? previous(sender, cert, chain, errors) : errors == SslPolicyErrors.None;
                return Validate(host, cert, chain, errors);
            };
        }

        internal static bool Validate(string host, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            bool forced = s_forceFallback && errors == SslPolicyErrors.None;
            if (errors == SslPolicyErrors.None && !forced) return true;
            if (host != null && !IsDiscordHost(host)) return false;   // other hosts: the default answer
            if (cert == null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) { NoteFailure(host, cert, chain, errors, "the certificate is for another name"); return false; }

            string why;
            bool ok = CheckWithBuiltInRoots(host, new X509Certificate2(cert), chain, out why);
            if (ok)
            {
                if (Interlocked.Exchange(ref s_fallbackNoted, 1) == 0)
                    Log($"[Caveman's Discord Bridge] {(forced ? "TLS test: " : "")}The server's certificate store rejected Discord ({errors}); Discord's certificate checks out against the bridge's built-in roots, so the bridge carries on. Restarting the server clears the store problem.");
            }
            else NoteFailure(host, cert, chain, errors, why);
            return ok;
        }

        private static bool CheckWithBuiltInRoots(string host, X509Certificate2 leaf, X509Chain presented, out string why)
        {
            DateTime now = DateTime.Now;
            if (now < leaf.NotBefore || now > leaf.NotAfter) { why = $"the certificate isn't valid now (valid {leaf.NotBefore:u} to {leaf.NotAfter:u}, server clock {DateTime.UtcNow:u} UTC)"; return false; }

            List<string> names = DnsNames(leaf);
            if (names.Count == 0) { why = "couldn't read the certificate's names"; return false; }
            if (host != null ? !names.Any(n => NameMatches(n, host)) : !names.Any(IsDiscordName)) { why = $"the certificate is for {string.Join(", ", names)}"; return false; }

            using (var build = new X509Chain())
            {
                build.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                build.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
                build.ChainPolicy.ExtraStore.AddRange(s_roots);
                if (presented != null)
                {
                    // Mono builds the chain it hands us from the certificates the server sent; take them all
                    try { build.ChainPolicy.ExtraStore.AddRange(presented.ChainPolicy.ExtraStore); } catch { }
                    try { foreach (X509ChainElement e in presented.ChainElements) build.ChainPolicy.ExtraStore.Add(e.Certificate); } catch { }
                }
                build.Build(leaf);

                const X509ChainStatusFlags allowed = X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain |
                                                     X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
                X509ChainElement[] elements = build.ChainElements.Cast<X509ChainElement>().ToArray();
                for (int i = 0; i < elements.Length; i++)
                {
                    // Reaching a built-in root's key is enough (GTS Root R4 also comes cross-signed by GlobalSign)
                    if (IsBuiltInRoot(elements[i].Certificate))
                    {
                        for (int j = 0; j <= i; j++)
                        {
                            X509ChainStatusFlags bad = elements[j].ChainElementStatus.Aggregate(X509ChainStatusFlags.NoError, (a, s) => a | s.Status) & ~allowed;
                            if (bad != X509ChainStatusFlags.NoError) { why = $"{elements[j].Certificate.Subject}: {bad}"; return false; }
                        }
                        why = null;
                        return true;
                    }
                }
                why = $"the chain doesn't reach a known root ({string.Join(" -> ", elements.Select(e => e.Certificate.GetNameInfo(X509NameType.SimpleName, false)))})";
                return false;
            }
        }

        private static bool IsBuiltInRoot(X509Certificate2 cert)
        {
            byte[] key = cert.GetPublicKey();
            foreach (X509Certificate2 root in s_roots)
                if (root.SubjectName.Name == cert.SubjectName.Name && root.GetPublicKey().SequenceEqual(key)) return true;
            return false;
        }

        private static List<string> DnsNames(X509Certificate2 cert)
        {
            var names = new List<string>();
            foreach (X509Extension ext in cert.Extensions)
            {
                if (ext.Oid?.Value != "2.5.29.17") continue;
                // Formatted as "DNS Name=discord.com, DNS Name=*.discord.com" (Windows) or "DNS:discord.com, ..." (others)
                foreach (Match m in Regex.Matches(ext.Format(false) ?? "", @"DNS(?: ?Name)?\s*[=:]\s*([^,\s]+)", RegexOptions.IgnoreCase))
                    names.Add(m.Groups[1].Value.ToLowerInvariant());
                if (names.Count == 0) names.AddRange(ParseSanDer(ext.RawData));
            }
            return names;
        }

        // SubjectAltName is a SEQUENCE of GeneralName; dNSName is [2] IA5String. Read it ourselves if Format() gave nothing.
        private static IEnumerable<string> ParseSanDer(byte[] d)
        {
            var result = new List<string>();
            try
            {
                int p = 0;
                if (d[p++] != 0x30) return result;
                int end = ReadLength(d, ref p) + p;
                while (p < end)
                {
                    byte tag = d[p++];
                    int len = ReadLength(d, ref p);
                    if (tag == 0x82) result.Add(System.Text.Encoding.ASCII.GetString(d, p, len).ToLowerInvariant());
                    p += len;
                }
            }
            catch { }
            return result;
        }

        private static int ReadLength(byte[] d, ref int p)
        {
            int b = d[p++];
            if (b < 0x80) return b;
            int n = b & 0x7f, len = 0;
            for (int i = 0; i < n; i++) len = (len << 8) | d[p++];
            return len;
        }

        private static bool NameMatches(string pattern, string host)
        {
            host = host.ToLowerInvariant().TrimEnd('.');
            if (pattern == host) return true;
            if (!pattern.StartsWith("*.")) return false;
            int dot = host.IndexOf('.');
            return dot > 0 && host.Substring(dot + 1) == pattern.Substring(2);
        }

        private static bool IsDiscordName(string name) => IsDiscordHost(name.StartsWith("*.") ? name.Substring(2) : name);

        internal static bool IsDiscordHost(string host)
        {
            host = host.ToLowerInvariant().TrimEnd('.');
            foreach (string d in new[] { "discord.com", "discord.gg", "discordapp.com" })
                if (host == d || host.EndsWith("." + d)) return true;
            return false;
        }

        private static void NoteFailure(string host, X509Certificate cert, X509Chain chain, SslPolicyErrors errors, string why)
        {
            if (Interlocked.Exchange(ref s_failureNoted, 1) == 1) return;
            string sent = "";
            try { if (chain != null) sent = string.Join(" -> ", chain.ChainElements.Cast<X509ChainElement>().Select(e => $"{e.Certificate.GetNameInfo(X509NameType.SimpleName, false)} [{string.Join(",", e.ChainElementStatus.Select(s => s.Status))}]")); } catch { }
            Log($"[Caveman's Discord Bridge] Discord's certificate for {host ?? "?"} was rejected: {why}. Store result: {errors}. Chain: {sent}. Server clock {DateTime.UtcNow:u} UTC. (Logged once.)");
        }
    }
}
