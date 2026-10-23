using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using FrostyCLI.Core;
using FrostyCLI.Commands;

namespace FrostyCLI.Tests
{
    // `game` is a socket client for FrostyGameLink: no game, no session. A fake link on a free port
    // stands in for the injected DLL (one command per connection, reply, then silence).
    public class GameCommandTests
    {
        private static int Run(params string[] args) =>
            new GameCommand().Execute(args, new ExportLogger(quiet: true), new OutputFormatter(false));

        [Test]
        public void Game_NoArgs_Returns2()
        {
            Assert.AreEqual(2, Run());
        }

        [Test]
        public void Game_UnknownSubcommand_Returns2()
        {
            Assert.AreEqual(2, Run("frobnicate"));
        }

        [Test]
        public void Game_FireWithoutTarget_Returns2()
        {
            Assert.AreEqual(2, Run("fire"));
            Assert.AreEqual(2, Run("deliver"));
        }

        [Test]
        public void Game_InspectAndSetWithoutTarget_Return2()
        {
            Assert.AreEqual(2, Run("inspect"));
            Assert.AreEqual(2, Run("set"));
        }

        [Test]
        public void Game_Controlled_ReadsThePlayersLine()
        {
            string players =
                "  0x32274770 'Gameplay/Settings/PlayerData' localId 1121391064 analog 0x00000001 digital 0x1\n" +
                "      controlled 0x32279890  cameraViewManager 0xCF98\n" +
                "  0x4DE072E0 'RAPID421' localId 0 analog 0x000000FF digital 0xFFFF\n" +
                "      controlled 0x1A2B  cameraViewManager 0x4B03AC90\n" +
                "  0x4ECD51F0 'Ben Astar' localId 255 analog 0x000000FF digital 0xFFFF\n" +
                "      controlled 0x10F2A83E0  cameraViewManager 0x0\n";
            Assert.AreEqual("0x1A2B", GameCommand.Controlled(players));
            Assert.IsNull(GameCommand.Controlled("no client game context"));
        }

        [Test]
        public void Game_ShotWithoutPosition_Returns2()
        {
            Assert.AreEqual(2, Run("shot"));
            Assert.AreEqual("1 -2.5 3", GameCommand.Spaced("1,-2.5,3"));
        }

        [Test]
        public void Game_SendWithoutCommand_Returns2()
        {
            Assert.AreEqual(2, Run("send"));
        }

        [Test]
        public void Game_NoLinkListening_Returns1()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();                                   // a port nothing listens on any more
            Assert.AreEqual(1, Run("status", "--port", port.ToString()));
        }

        // diff works on recordings alone - no link needed.
        private static string DiffOutput(string[] a, string[] b, params string[] extra)
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frostycli-game-diff-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string pa = System.IO.Path.Combine(dir, "a.txt"), pb = System.IO.Path.Combine(dir, "b.txt");
            System.IO.File.WriteAllLines(pa, a);
            System.IO.File.WriteAllLines(pb, b);
            var previous = System.Console.Out;
            var capture = new System.IO.StringWriter();
            System.Console.SetOut(capture);
            try
            {
                var args = new System.Collections.Generic.List<string> { "diff", pa, pb };
                args.AddRange(extra);
                Assert.AreEqual(0, Run(args.ToArray()));
            }
            finally
            {
                System.Console.SetOut(previous);
                System.IO.Directory.Delete(dir, true);
            }
            return capture.ToString();
        }

        [Test]
        public void Game_Diff_NamesFirstDivergence()
        {
            string output = DiffOutput(
                new[] { "0.100 S X[1].Out -> Y[2].In  @bp", "0.200 S Y[2].Out -> Z[3].StreamIn  @bp" },
                new[] { "0.150 S X[1].Out -> Y[2].In  @bp" });
            Assert.IsTrue(output.Contains("first divergence"), output);
            Assert.IsTrue(output.Contains("Y[2].Out -> Z[3].StreamIn  @bp"), output);
            Assert.IsTrue(output.Contains("only in B (0)"), output);
        }

        [Test]
        public void Game_Diff_Loose_IgnoresInstanceTagsAndBlueprint()
        {
            string output = DiffOutput(
                new[] { "0.1 S X[1].Out -> Y#1a2b3c4d.In  @Levels/Vanilla/GameModes_Flow" },
                new[] { "0.1 S X[7].Out -> Y[9].In  @Levels/MyPort/GameModes_Flow" }, "--loose");
            Assert.IsTrue(output.Contains("shared 1"), output);
            Assert.IsTrue(output.Contains("only in A (0)"), output);
        }

        [Test]
        public void Game_Diff_PropertyValuesAreNotDifferences()
        {
            string output = DiffOutput(
                new[] { "1.0 S A[1].F -> B[2].G = 3.5  @bp" },
                new[] { "2.0 C A[1].F (+1) -> B[2].G = 9  @bp" });
            Assert.IsTrue(output.Contains("shared 1"), output);
        }

        [Test]
        public void Game_Timeline_ListsStreamAndModeWiresFolded()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frostycli-timeline-" + System.Guid.NewGuid().ToString("N") + ".txt");
            System.IO.File.WriteAllLines(path, new[]
            {
                "1.0 S SyncedBool#1.OnTrue -> SubWorldReferenceObject#2.StreamIn  @L/GameModes",
                "1.0 S Delay[3].Out -> EventGate[4].In  @L/GameModes",
                "2.0 S SubWorldReferenceObject#2.OnStreamedIn -> EventGate#5.In  @L/GameModes",
                "3.0 S SyncedBool#1.OnTrue -> SubWorldReferenceObject#2.StreamIn  @L/GameModes",
            });
            var previous = System.Console.Out;
            var capture = new System.IO.StringWriter();
            System.Console.SetOut(capture);
            try { Assert.AreEqual(0, Run("timeline", path)); }
            finally { System.Console.SetOut(previous); System.IO.File.Delete(path); }
            string output = capture.ToString();
            Assert.IsTrue(output.Contains("SubWorldReferenceObject#2.StreamIn  @L/GameModes   (x2)"), output);
            Assert.IsTrue(output.Contains("OnStreamedIn -> EventGate#5.In"), output);
            Assert.IsTrue(!output.Contains("Delay[3].Out"), output);
        }

        [Test]
        public void Game_Crash_ExplainsFromStackAndShowsFeed()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frostycli-game-crash-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string log = System.IO.Path.Combine(dir, "gamelink.log"), known = System.IO.Path.Combine(dir, "known.json");
            System.IO.File.WriteAllLines(log, new[]
            {
                "[10:00:00.000] ---- FrostyGameLink attached ----",
                "[10:05:00.000] ---- process is going down ----",
                "[10:05:00.000] crash: code 0xC0000005 ACCESS_VIOLATION at game+0x1234567 (thread 42)",
                "[10:05:00.000] crash:   stack+0x260 -> game+0x816376",
                "[10:05:00.000] feed: last 2 logic events (trace on)",
                "[10:05:00.000] feed: S SyncedBool#6f08e831.OnTrue -> SubWorldReferenceObject#55a4757e.StreamIn  @MyPort/GameModes",
            });
            System.IO.File.WriteAllText(known,
                "{\"crashes\":[{\"address\":\"game+0x816376\",\"cause\":\"worker-thread job\",\"fix\":\"look at the worker\",\"confidence\":\"confirmed\"}]}");
            var previous = System.Console.Out;
            var capture = new System.IO.StringWriter();
            System.Console.SetOut(capture);
            try { Assert.AreEqual(0, Run("crash", "--log", log, "--known", known)); }
            finally { System.Console.SetOut(previous); System.IO.Directory.Delete(dir, true); }
            string output = capture.ToString();
            Assert.IsTrue(output.Contains("CRASH at 10:05:00.000"), output);
            Assert.IsTrue(output.Contains("at game+0x1234567"), output);
            Assert.IsTrue(output.Contains("KNOWN (game+0x816376, on the stack)"), output);
            Assert.IsTrue(output.Contains("SubWorldReferenceObject#55a4757e.StreamIn"), output);
        }

        [Test]
        public void Game_Send_DeliversCommandAndReadsReply()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string received = null;
            var server = new Thread(() =>
            {
                using (var client = listener.AcceptTcpClient())
                {
                    var stream = client.GetStream();
                    var buffer = new byte[256];
                    int n = stream.Read(buffer, 0, buffer.Length);
                    received = Encoding.ASCII.GetString(buffer, 0, n);
                    byte[] reply = Encoding.ASCII.GetBytes("pong gamelink\n");
                    stream.Write(reply, 0, reply.Length);
                    Thread.Sleep(1500);                     // stay open and quiet, like the real link
                }
            });
            server.Start();
            int rc = Run("send", "ping", "--port", port.ToString());
            server.Join();
            listener.Stop();
            Assert.AreEqual(0, rc);
            Assert.AreEqual("ping\n", received);
        }
    }
}
