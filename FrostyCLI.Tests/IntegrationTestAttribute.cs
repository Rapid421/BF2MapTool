using System;
namespace FrostyCLI.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    public class IntegrationTestAttribute : Attribute
    {
        public string Game { get; }
        // Slow tests (e.g. those that build the full 155k-asset reference index, ~83s) are
        // skipped by default and only run when the test runner is passed --slow.
        public bool Slow { get; set; }
        public IntegrationTestAttribute() { }
        public IntegrationTestAttribute(string game) { Game = game; }
    }
}
