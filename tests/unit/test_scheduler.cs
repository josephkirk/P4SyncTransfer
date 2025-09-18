using System;
using System.Threading.Tasks;
using P4Sync;
using Xunit;
using Moq;
using Microsoft.Extensions.Logging;

namespace P4Sync.Tests.Unit
{
    public class TestScheduler
    {
        [Fact]
        public async Task TestCronScheduling()
        {
            var triggered = false;
            Action action = () => triggered = true;

            var mockLogger = new Mock<ILogger<Scheduler>>();
            var scheduler = new Scheduler("* * * * *", action, mockLogger.Object);
            scheduler.Start();

            await Task.Delay(TimeSpan.FromSeconds(61)); // Wait for a minute and a second

            scheduler.Stop();

            Assert.True(triggered);
        }
    }
}
