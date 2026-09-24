using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests.Utils;

/// <summary>
/// Records scenario steps so that a failing assertion reports the first violation
/// position together with the context captured up to that point, instead of a bare boolean.
/// </summary>
public sealed class ScenarioLog
{
	private readonly List<string> Entries = new();

	public void Record(string step, string detail)
	{
		lock (Entries)
		{
			Entries.Add($"[{Entries.Count:D2}] {step}: {detail}");
		}
	}

	public override string ToString()
	{
		lock (Entries)
		{
			var builder = new StringBuilder();
			foreach (var entry in Entries)
			{
				builder.AppendLine(entry);
			}

			return builder.ToString();
		}
	}

	public void AreEqual<T>(T expected, T actual, string step)
	{
		Assert.AreEqual(expected, actual, $"First violation at step '{step}'.{Environment.NewLine}Scenario context up to failure:{Environment.NewLine}{this}");
	}

	public void IsTrue(bool condition, string step, string detail)
	{
		Record(step, detail);
		Assert.IsTrue(condition, $"First violation at step '{step}': {detail}.{Environment.NewLine}Scenario context up to failure:{Environment.NewLine}{this}");
	}

	public void Fail(string step, string detail)
	{
		Assert.Fail($"First violation at step '{step}': {detail}.{Environment.NewLine}Scenario context up to failure:{Environment.NewLine}{this}");
	}
}
