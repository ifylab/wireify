// SPDX-License-Identifier: Apache-2.0
using System;
using System.Linq;
using WireifyCore.Connect;

namespace WireifyCore.Tests;

public class InstallLocationsTests
{
    const string Manual = @"C:\Users\u\AppData\Roaming\Grasshopper\Libraries\Wireify";
    const string Package = @"C:\Users\u\AppData\Roaming\McNeel\Rhinoceros\packages\8.0\wireify";

    [Fact]
    public void A_single_install_raises_nothing()
    {
        Assert.Empty(InstallLocations.Warnings(Package + @"\0.1.1\net7.0\WireifyCore.dll", new[] { Package }));
    }

    [Fact]
    public void Two_installs_warn_about_the_one_not_running()
    {
        var warnings = InstallLocations.Warnings(
            Package + @"\0.1.1\net7.0\WireifyCore.dll", new[] { Manual, Package });

        var warning = Assert.Single(warnings);
        Assert.Contains(Manual, warning);
        Assert.Contains("ID already in use", warning);
    }

    [Fact]
    public void Running_from_the_manual_copy_warns_about_the_package_copy()
    {
        var warnings = InstallLocations.Warnings(
            Manual + @"\net7.0\WireifyCore.dll", new[] { Manual, Package });

        Assert.Contains(Package, Assert.Single(warnings));
    }

    [Fact]
    public void Unknown_running_location_warns_about_every_root()
    {
        Assert.Equal(2, InstallLocations.Warnings(null, new[] { Manual, Package }).Count);
    }

    [Fact]
    public void Probe_finds_manual_and_per_version_package_roots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var manual = System.IO.Path.Combine(appData, "Grasshopper", "Libraries", "Wireify");
        var packages = System.IO.Path.Combine(appData, "McNeel", "Rhinoceros", "packages");
        var v7 = System.IO.Path.Combine(packages, "7.0");
        var v8 = System.IO.Path.Combine(packages, "8.0");

        var roots = InstallLocations.ExistingRoots(
            dirExists: path => path == manual || path == packages
                || path == System.IO.Path.Combine(v8, "wireify"),
            subdirs: _ => new[] { v7, v8 }).ToList();

        Assert.Equal(2, roots.Count);
        Assert.Contains(manual, roots);
        Assert.Contains(System.IO.Path.Combine(v8, "wireify"), roots);
    }

    [Fact]
    public void Probe_handles_an_unreadable_packages_tree()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var packages = System.IO.Path.Combine(appData, "McNeel", "Rhinoceros", "packages");

        var roots = InstallLocations.ExistingRoots(
            dirExists: path => path == packages,
            subdirs: _ => throw new UnauthorizedAccessException()).ToList();

        Assert.Empty(roots);
    }
}
