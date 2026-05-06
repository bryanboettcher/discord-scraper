using DiscordScraper.TestSupport.Stubs;

namespace DiscordScraper.Enrichment.Tests.Stubs;

[TestFixture]
public class FailureProfileTests
{
    [Test]
    public void None_NeverFaults()
    {
        var profile = new FailureProfile<string>.None();
        for (int i = 1; i <= 100; i++)
        {
            var fault = profile.FaultFor("test", i);
            Assert.That(fault, Is.Null);
        }
    }

    [Test]
    public void EveryNth_FaultsAtCorrectIntervals()
    {
        var exceptionFactory = () => new InvalidOperationException("Test failure");
        var profile = new FailureProfile<string>.EveryNth(3, exceptionFactory);

        var faulted = new List<int>();
        for (int i = 1; i <= 10; i++)
        {
            var fault = profile.FaultFor("test", i);
            if (fault != null)
                faulted.Add(i);
        }

        Assert.That(faulted, Is.EqualTo(new[] { 3, 6, 9 }));
    }

    [Test]
    public void EveryNth_InvalidN_Throws()
    {
        var exceptionFactory = () => new InvalidOperationException("Test failure");
        var profile = new FailureProfile<string>.EveryNth(0, exceptionFactory);
        Assert.Throws<ArgumentException>(() => profile.FaultFor("test", 1));
    }

    [Test]
    public void Burst_FaultsAfterNormalCalls()
    {
        var exceptionFactory = () => new InvalidOperationException("Test failure");
        var profile = new FailureProfile<string>.Burst(AfterCalls: 5, Count: 3, exceptionFactory);

        var faulted = new List<int>();
        for (int i = 1; i <= 10; i++)
        {
            var fault = profile.FaultFor("test", i);
            if (fault != null)
                faulted.Add(i);
        }

        Assert.That(faulted, Is.EqualTo(new[] { 6, 7, 8 }));
    }

    [Test]
    public void Burst_InvalidAfterCalls_Throws()
    {
        var exceptionFactory = () => new InvalidOperationException("Test failure");
        var profile = new FailureProfile<string>.Burst(-1, 3, exceptionFactory);
        Assert.Throws<ArgumentException>(() => profile.FaultFor("test", 1));
    }

    [Test]
    public void Burst_InvalidCount_Throws()
    {
        var exceptionFactory = () => new InvalidOperationException("Test failure");
        var profile = new FailureProfile<string>.Burst(5, 0, exceptionFactory);
        Assert.Throws<ArgumentException>(() => profile.FaultFor("test", 1));
    }

    [Test]
    public void Window_FaultsInRange()
    {
        var exceptionFactory = () => new InvalidOperationException("Test failure");
        var profile = new FailureProfile<string>.Window(StartCall: 3, EndCall: 5, exceptionFactory);

        var faulted = new List<int>();
        for (int i = 1; i <= 10; i++)
        {
            var fault = profile.FaultFor("test", i);
            if (fault != null)
                faulted.Add(i);
        }

        Assert.That(faulted, Is.EqualTo(new[] { 3, 4, 5 }));
    }

    [Test]
    public void Window_InvalidRange_Throws()
    {
        var exceptionFactory = () => new InvalidOperationException("Test failure");
        var profile = new FailureProfile<string>.Window(StartCall: 5, EndCall: 3, exceptionFactory);
        Assert.Throws<ArgumentException>(() => profile.FaultFor("test", 1));
    }

    [Test]
    public void FromInput_ReceivesInputAndCallIndex()
    {
        var calls = new List<(string input, int index)>();
        var exceptionFactory = () => new InvalidOperationException("Test failure");

        var profile = new FailureProfile<string>.FromInput((input, idx) =>
        {
            calls.Add((input, idx));
            return input.StartsWith("fail") ? exceptionFactory() : null;
        });

        _ = profile.FaultFor("test", 1);
        _ = profile.FaultFor("fail-now", 2);

        Assert.That(calls.Count, Is.EqualTo(2));
        Assert.That(calls[0].input, Is.EqualTo("test"));
        Assert.That(calls[0].index, Is.EqualTo(1));
        Assert.That(calls[1].input, Is.EqualTo("fail-now"));
        Assert.That(calls[1].index, Is.EqualTo(2));
    }

    [Test]
    public void Burst_KillSwitchScenario()
    {
        var exceptionFactory = () => new InvalidOperationException("KillSwitch trip");
        var profile = new FailureProfile<string>.Burst(AfterCalls: 50, Count: 12, exceptionFactory);

        var goodCount = 0;
        var badCount = 0;

        for (int i = 1; i <= 70; i++)
        {
            if (profile.FaultFor("msg", i) == null)
                goodCount++;
            else
                badCount++;
        }

        // Calls 1-50 succeed, calls 51-62 fail, calls 63-70 succeed
        Assert.That(goodCount, Is.EqualTo(58));
        Assert.That(badCount, Is.EqualTo(12));
    }
}
