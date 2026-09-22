using TheOverseer.Core.Models;

namespace TheOverseer.Core.Monitoring.Parsing;

public sealed record CommandSignal(AgentActionKind Action, string DetailCode);

/// <summary>
/// Narrow structural command heuristic from the frozen specification's
/// "command heuristic" priority. The command string is transient parser input:
/// it is inspected in memory with a hard length and token ceiling, never stored,
/// logged, echoed or searched for free-form prose.
/// </summary>
public static class CommandClassifier
{
    public const int MaximumCommandChars = 512;
    private const int MaximumTokens = 32;
    private const int MaximumTokenChars = 64;

    private static readonly HashSet<string> TestRunners = new(StringComparer.OrdinalIgnoreCase)
    {
        "pytest", "jest", "vitest", "ctest", "mocha", "rspec", "phpunit", "nosetests", "tox",
        "py.test", "nunit", "xunit",
    };

    private static readonly HashSet<string> TestVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "vstest", "spec", "specs",
    };

    private static readonly HashSet<string> TestHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "dotnet", "npm", "pnpm", "yarn", "npx", "go", "cargo", "mvn", "gradle", "gradlew", "make",
        "rake", "bazel", "python", "python3", "uv", "poetry", "node", "deno", "bun", "msbuild",
    };

    private static readonly HashSet<string> SearchCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "rg", "grep", "findstr", "select-string", "ag", "ack", "fd", "find", "where", "which",
        "git-grep", "gitgrep", "ripgrep",
    };

    private static readonly HashSet<string> ReadCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cat", "bat", "type", "head", "tail", "less", "more", "get-content", "ls", "dir",
        "get-childitem", "tree", "wc", "nl", "stat", "file",
    };

    public static CommandSignal? Classify(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var bounded = command.Length > MaximumCommandChars ? command[..MaximumCommandChars] : command;
        var tokens = Tokenize(bounded);
        if (tokens.Count == 0)
        {
            return null;
        }

        var first = tokens[0];
        var second = tokens.Count > 1 ? tokens[1] : string.Empty;

        if (TestRunners.Contains(first))
        {
            return new CommandSignal(AgentActionKind.RunningTests, "test-runner");
        }

        // "dotnet test", "npm run test", "python -m pytest" and friends.
        if (TestHosts.Contains(first))
        {
            if (TestVerbs.Contains(second))
            {
                return new CommandSignal(AgentActionKind.RunningTests, "test-runner");
            }

            if (tokens.Count > 2)
            {
                var third = tokens[2];
                if (second.Equals("run", StringComparison.OrdinalIgnoreCase) && TestVerbs.Contains(third))
                {
                    return new CommandSignal(AgentActionKind.RunningTests, "test-runner");
                }

                if (second.Equals("-m", StringComparison.OrdinalIgnoreCase) &&
                    (TestRunners.Contains(third) || TestVerbs.Contains(third)))
                {
                    return new CommandSignal(AgentActionKind.RunningTests, "test-runner");
                }
            }
        }

        if (SearchCommands.Contains(first))
        {
            return new CommandSignal(AgentActionKind.Searching, "search-command");
        }

        if (ReadCommands.Contains(first))
        {
            return new CommandSignal(AgentActionKind.ReadingFiles, "read-command");
        }

        return null;
    }

    private static List<string> Tokenize(string command)
    {
        var tokens = new List<string>(MaximumTokens);
        var index = 0;
        while (index < command.Length && tokens.Count < MaximumTokens)
        {
            while (index < command.Length && IsSeparator(command[index]))
            {
                index++;
            }

            var start = index;
            while (index < command.Length && !IsSeparator(command[index]))
            {
                index++;
            }

            if (index > start)
            {
                tokens.Add(Normalize(command[start..index]));
            }
        }

        return tokens;
    }

    private static bool IsSeparator(char character) =>
        char.IsWhiteSpace(character) || character is ';' or '|' or '&' or '(' or ')' or '=' or ',';

    private static string Normalize(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length >= 2 && (trimmed[0] == '"' || trimmed[0] == '\'') && trimmed[^1] == trimmed[0])
        {
            trimmed = trimmed[1..^1];
        }

        var separator = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        if (separator >= 0 && separator + 1 < trimmed.Length)
        {
            trimmed = trimmed[(separator + 1)..];
        }

        foreach (var extension in new[] { ".exe", ".cmd", ".bat", ".ps1", ".sh" })
        {
            if (trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^extension.Length];
                break;
            }
        }

        return trimmed.Length <= MaximumTokenChars ? trimmed : trimmed[..MaximumTokenChars];
    }
}
