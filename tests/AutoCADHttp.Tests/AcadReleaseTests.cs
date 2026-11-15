using System;
using AutoCADHttp;
using Xunit;

namespace AutoCADHttp.Tests
{
    public class AcadReleaseTests
    {
        [Theory]
        [InlineData("23.1.47.0", "2020")]
        [InlineData("24.0.47.0", "2021")]
        [InlineData("24.1.51.0", "2022")]
        [InlineData("24.2.0.0", "2023")]
        [InlineData("24.3.0.0", "2024")]
        public void SupportedReleasesAreReportedAsProductYear(string version, string expected)
        {
            Assert.Equal(expected, AcadRelease.YearName(Version.Parse(version)));
        }

        [Fact]
        public void UnknownReleaseIsReportedAsReleaseNumber()
        {
            Assert.Equal("R25.0", AcadRelease.YearName(new Version(25, 0)));
        }

        [Fact]
        public void MissingVersionFallsBackToTheApiVersion()
        {
            Assert.Equal("2021", AcadRelease.YearName(null));
        }
    }
}
