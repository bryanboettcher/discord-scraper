using DiscordScraper.Enrichment.Ollama;
using DiscordScraper.TestSupport.Stubs;

namespace DiscordScraper.Enrichment.Tests.Stubs;

[TestFixture]
public class OutputGeneratorTests
{
    [Test]
    public void Fixed_ReturnsSameValueAlways()
    {
        var value = "constant-output";
        var generator = new OutputGenerator<string, string>.Fixed(value);

        var result1 = generator.Generate("input1");
        var result2 = generator.Generate("input2");
        var result3 = generator.Generate("input3");

        Assert.That(result1, Is.EqualTo(value));
        Assert.That(result2, Is.EqualTo(value));
        Assert.That(result3, Is.EqualTo(value));
    }

    [Test]
    public void FromInput_ProducesDeterministicOutput()
    {
        var generator = new OutputGenerator<string, int>.FromInput(s => s.Length * 10);

        var result1 = generator.Generate("hello");
        var result2 = generator.Generate("hello");
        var result3 = generator.Generate("hi");

        Assert.That(result1, Is.EqualTo(50));
        Assert.That(result2, Is.EqualTo(50));
        Assert.That(result3, Is.EqualTo(20));
    }

    [Test]
    public void FromInput_BitIdenticalAcrossCalls()
    {
        var generator = new OutputGenerator<string, string>.FromInput(s => $"processed:{s}");

        var outputs = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            outputs.Add(generator.Generate("test-input"));
        }

        Assert.That(outputs[0], Is.EqualTo("processed:test-input"));
        Assert.That(outputs[1], Is.EqualTo("processed:test-input"));
        Assert.That(outputs[2], Is.EqualTo("processed:test-input"));
    }

    [Test]
    public void Sequence_CyclesThroughValues()
    {
        var values = new[] { "a", "b", "c" };
        var generator = new OutputGenerator<string, string>.Sequence(values);

        var result1 = generator.Generate("input");
        var result2 = generator.Generate("input");
        var result3 = generator.Generate("input");
        var result4 = generator.Generate("input");

        Assert.That(result1, Is.EqualTo("a"));
        Assert.That(result2, Is.EqualTo("b"));
        Assert.That(result3, Is.EqualTo("c"));
        Assert.That(result4, Is.EqualTo("a"));
    }

    [Test]
    public void Sequence_EmptySequence_Throws()
    {
        var generator = new OutputGenerator<string, string>.Sequence(Array.Empty<string>());
        Assert.Throws<InvalidOperationException>(() => generator.Generate("input"));
    }

    [Test]
    public void Bogus_UsesSeedForDeterminism()
    {
        var builder = (Bogus.Faker faker, string _) => faker.Random.Int(0, 100);
        var gen1 = new OutputGenerator<string, int>.Bogus(42, builder);
        var gen2 = new OutputGenerator<string, int>.Bogus(42, builder);

        var result1 = gen1.Generate("test");
        var result2 = gen2.Generate("test");

        Assert.That(result1, Is.EqualTo(result2));
    }

    [Test]
    public void Bogus_DifferentSeeds_ProduceDifferentOutputs()
    {
        var builder = (Bogus.Faker faker, string _) => faker.Random.Int(0, 1000000);
        var gen1 = new OutputGenerator<string, int>.Bogus(42, builder);
        var gen2 = new OutputGenerator<string, int>.Bogus(43, builder);

        var result1 = gen1.Generate("test");
        var result2 = gen2.Generate("test");

        Assert.That(result1, Is.Not.EqualTo(result2));
    }
}

[TestFixture]
public class DeterministicEmbeddingTests
{
    [Test]
    public void DeterministicEmbedding_ProducesSameOutputForSameInput()
    {
        var generator = OutputGeneratorHelpers.DeterministicEmbedding(768);

        var output1 = generator.Generate("hello world");
        var output2 = generator.Generate("hello world");

        Assert.That(output1.Length, Is.EqualTo(768));
        Assert.That(output2.Length, Is.EqualTo(768));
        Assert.That(output1.Span.SequenceEqual(output2.Span), Is.True);
    }

    [Test]
    public void DeterministicEmbedding_ProducesDifferentOutputForDifferentInput()
    {
        var generator = OutputGeneratorHelpers.DeterministicEmbedding(768);

        var output1 = generator.Generate("hello");
        var output2 = generator.Generate("world");

        Assert.That(output1.Span.SequenceEqual(output2.Span), Is.False);
    }

    [Test]
    public void DeterministicEmbedding_DefaultDimension_Is768()
    {
        var generator = OutputGeneratorHelpers.DeterministicEmbedding();
        var output = generator.Generate("test");
        Assert.That(output.Length, Is.EqualTo(768));
    }

    [Test]
    public void DeterministicEmbedding_CustomDimension()
    {
        var generator = OutputGeneratorHelpers.DeterministicEmbedding(512);
        var output = generator.Generate("test");
        Assert.That(output.Length, Is.EqualTo(512));
    }
}

[TestFixture]
public class DeterministicTagsTests
{
    [Test]
    public void DeterministicTags_ProducesSameOutputForSameInput()
    {
        var generator = OutputGeneratorHelpers.DeterministicTags(3);

        var result1 = generator.Generate("hello world");
        var result2 = generator.Generate("hello world");

        Assert.That(result1.TopicTags, Is.EqualTo(result2.TopicTags));
        Assert.That(result1.IsSubstantive, Is.EqualTo(result2.IsSubstantive));
    }

    [Test]
    public void DeterministicTags_DefaultCount_Is3()
    {
        var generator = OutputGeneratorHelpers.DeterministicTags();
        var result = generator.Generate("test");
        Assert.That(result.TopicTags.Count, Is.EqualTo(3));
    }

    [Test]
    public void DeterministicTags_CustomCount()
    {
        var generator = OutputGeneratorHelpers.DeterministicTags(5);
        var result = generator.Generate("test");
        Assert.That(result.TopicTags.Count, Is.EqualTo(5));
    }

    [Test]
    public void DeterministicTags_InvalidCountZero_Throws()
    {
        Assert.Throws<ArgumentException>(() => OutputGeneratorHelpers.DeterministicTags(0));
    }

    [Test]
    public void DeterministicTags_InvalidCountTooHigh_Throws()
    {
        Assert.Throws<ArgumentException>(() => OutputGeneratorHelpers.DeterministicTags(11));
    }

    [Test]
    public void DeterministicTags_IsSubstantiveWhenTagsPresent()
    {
        var generator = OutputGeneratorHelpers.DeterministicTags(3);
        var result = generator.Generate("test");
        Assert.That(result.IsSubstantive, Is.True);
    }
}
