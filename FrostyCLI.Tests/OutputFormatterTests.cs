using System.Collections.Generic;
using System.IO;
using FrostyCLI.Core;
using Newtonsoft.Json.Linq;

namespace FrostyCLI.Tests
{
    public class OutputFormatterTests
    {
        [Test]
        public void HumanFormat_ContainsTimestampStatusAndOp()
        {
            var sw = new StringWriter();
            var fmt = new OutputFormatter(json: false, output: sw);
            var fields = new Dictionary<string, string>();
            fields["file"] = "mesh.fbx";
            fmt.WriteResult("OK", "export", fields);
            string line = sw.ToString();
            Assert.IsTrue(line.Contains("OK"), "Should contain status");
            Assert.IsTrue(line.Contains("export"), "Should contain op");
            Assert.IsTrue(line.Contains("mesh.fbx"), "Should contain file");
            Assert.IsTrue(line.Contains("["), "Should contain timestamp bracket");
        }

        [Test]
        public void HumanFormat_ExtraFieldsOnSubsequentLines()
        {
            var sw = new StringWriter();
            var fmt = new OutputFormatter(json: false, output: sw);
            var fields = new Dictionary<string, string>();
            fields["file"]  = "mesh.fbx";
            fields["error"] = "not found";
            fmt.WriteResult("FAIL", "export", fields);
            string output = sw.ToString();
            Assert.IsTrue(output.Contains("error"), "Extra field key should appear");
            Assert.IsTrue(output.Contains("not found"), "Extra field value should appear");
        }

        [Test]
        public void JsonFormat_IsValidJson()
        {
            var sw = new StringWriter();
            var fmt = new OutputFormatter(json: true, output: sw);
            var fields = new Dictionary<string, string>();
            fields["file"] = "mesh.fbx";
            fmt.WriteResult("OK", "export", fields);
            string line = sw.ToString().Trim();
            var obj = JObject.Parse(line);
            Assert.AreEqual("OK", (string)obj["status"]);
            Assert.AreEqual("export", (string)obj["op"]);
            Assert.AreEqual("mesh.fbx", (string)obj["file"]);
        }

        [Test]
        public void JsonFormat_ContainsTimestamp()
        {
            var sw = new StringWriter();
            var fmt = new OutputFormatter(json: true, output: sw);
            fmt.WriteResult("OK", "ls", null);
            var obj = JObject.Parse(sw.ToString().Trim());
            Assert.IsNotNull((string)obj["ts"]);
        }

        [Test]
        public void JsonFormat_NullFields_DoesNotCrash()
        {
            var sw = new StringWriter();
            var fmt = new OutputFormatter(json: true, output: sw);
            fmt.WriteResult("OK", "plugins", null);
            var obj = JObject.Parse(sw.ToString().Trim());
            Assert.AreEqual("plugins", (string)obj["op"]);
        }

        [Test]
        public void HumanFormat_NullFields_DoesNotCrash()
        {
            var sw = new StringWriter();
            var fmt = new OutputFormatter(json: false, output: sw);
            fmt.WriteResult("OK", "plugins", null);
            string line = sw.ToString();
            Assert.IsTrue(line.Contains("plugins"));
        }
    }
}
