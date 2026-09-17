// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;
using System.IO;

namespace WireifyCore.Connect
{
    /// <summary>What one scaffold call actually did — the action half of scaffold_app's receipt
    /// (state alone let the calling agent tell the user its page pre-existed when the call had
    /// just seeded it).</summary>
    public sealed record AppScaffoldReport(
        IReadOnlyList<string> Seeded, IReadOnlyList<string> Skipped, string Template);

    /// <summary>
    /// Stamps the companion-app kit into a home. Ownership is the whole design: everything under
    /// <c>app/kit/</c> is Wireify's and is overwritten per file (the skills-tree posture — kit
    /// updates propagate, a copied app folder keeps working); <c>app/index.html</c>,
    /// <c>app/manifest.json</c>, and <c>app/theme.css</c> belong to the agent/user and are only
    /// ever seeded when absent. The full scaffold runs from the <c>scaffold_app</c> tool; the
    /// Connect-time refresh only touches homes where an app already exists, so appless homes
    /// stay lean.
    /// </summary>
    public static class AppKitScaffolder
    {
        const string EmptyManifest = "{\n  \"controls\": [],\n  \"views\": []\n}\n";

        /// <summary>Full scaffold (the tool path): create <c>app/</c>, stamp <c>kit/</c>, seed
        /// the chosen page template and an empty manifest only where absent.
        /// <paramref name="template"/> picks the seeded page shape — "panel" (the control-panel
        /// starter) or "report" (the live-report shape: hero viewport, KPI strip, views,
        /// controls drawer) — and only matters when <c>index.html</c> does not exist yet; an
        /// existing page is never replaced, whatever template is asked for.</summary>
        public static AppScaffoldReport Scaffold(string templateRoot, string homeDir, string template = "panel")
        {
            var appDir = Path.Combine(homeDir, "app");
            Directory.CreateDirectory(appDir);
            StampKit(templateRoot, appDir);

            var seeded = new List<string>();
            var skipped = new List<string>();

            var index = Path.Combine(appDir, "index.html");
            if (File.Exists(index))
            {
                skipped.Add("index.html");
            }
            else
            {
                var page = Path.Combine(templateRoot, "app",
                    template == "report" ? "report.html" : "starter.html");
                if (File.Exists(page))
                {
                    File.Copy(page, index);
                    seeded.Add("index.html");
                }
            }

            var manifest = Path.Combine(appDir, "manifest.json");
            if (File.Exists(manifest)) skipped.Add("manifest.json");
            else { File.WriteAllText(manifest, EmptyManifest); seeded.Add("manifest.json"); }

            // The user-owned re-theme file, seeded empty so the starter's <link> never 404s and
            // re-theming is just editing it. Never touched again once it exists.
            var theme = Path.Combine(appDir, "theme.css");
            if (File.Exists(theme))
            {
                skipped.Add("theme.css");
            }
            else
            {
                File.WriteAllText(theme,
                    "/* Your app's re-theme file - loaded after the kit CSS, so any --ify-* token\n"
                    + "   redefined here wins. Edit freely; Wireify never touches this file. Kit\n"
                    + "   files under kit/ are refreshed on Connect - re-theme here, never there. */\n");
                seeded.Add("theme.css");
            }

            return new AppScaffoldReport(seeded, skipped, template);
        }

        /// <summary>Connect-time refresh: re-stamp <c>kit/</c> only when the home already has an
        /// <c>app/</c> dir. Never creates the app, never touches agent/user files.</summary>
        public static void RefreshKit(string templateRoot, string homeDir)
        {
            var appDir = Path.Combine(homeDir, "app");
            if (!Directory.Exists(appDir)) return;
            StampKit(templateRoot, appDir);
        }

        static void StampKit(string templateRoot, string appDir)
            => HomeScaffolder.CopyTree(Path.Combine(templateRoot, "app", "kit"), Path.Combine(appDir, "kit"));
    }
}
