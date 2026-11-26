using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Upipe.Editor
{
    internal sealed class CommandParameter
    {
        public string Name;
        public bool Required;
        public bool IsBool;
        // Bound as the string the caller sent (replies echo it that way); only checked for being an integer.
        public bool IsInteger;
        // Allows repeated --name flags in argv (collected into a JArray) and a string or JArray of strings in structured calls.
        // The wire type stays String: `unity mcp` builds tool schemas from it alone, and an array there has no `items`,
        // which some MCP clients refuse. The unity CLI itself refuses a repeated flag, so its callers send one string
        // with separators; repeated flags only come from raw argv requests.
        public bool Repeatable;
        public string Description = "";

        public string WireType => IsBool ? "Boolean" : IsInteger ? "Int64" : "String";
        public string SchemaType => IsBool ? "boolean" : IsInteger ? "integer" : "string";
    }

    internal sealed class CommandEntry
    {
        public string Group;
        public string Name;
        // Single-token name the unity CLI and its skill docs use ("editor_status", "eval"). It resolves in argv and
        // structured requests and is the catalog's `name`, which `unity mcp` turns into the tool name, so it must be
        // unique where Name alone is not ("status" is both editor status and console status).
        public string Alias;
        public string Description = "";
        public string[] Tags = Array.Empty<string>();
        // Declaration order is wire API: positionals bind to required parameters first, then optional ones.
        public CommandParameter[] Parameters = Array.Empty<CommandParameter>();
        public bool MainThreadRequired = true;
        // Refused with 503 while the Editor is still settling, even though the handler starts off the main thread.
        public bool WaitsForSettle;
        // Used when neither the request's top-level timeout nor a `timeout` parameter is given.
        public int DefaultTimeoutSeconds = 60;
        public bool ReadOnly;
        public Func<JObject, object> Handler;
        // Used instead of Handler when set: the context's token is cancelled when the request times out, so a handler
        // can stop waiting and skip work that has not started yet.
        public Func<JObject, CommandContext, object> CancellableHandler;

        public string FullName => Group + " " + Name;
        public string CatalogName => Alias ?? Name;

        public JObject ToCatalogEntry()
        {
            return new JObject
            {
                ["command"] = FullName,
                ["name"] = CatalogName,
                ["group"] = Group,
                ["description"] = Description,
                ["tags"] = new JArray(Tags),
                ["package"] = "Upipe.Editor",
                ["readOnly"] = ReadOnly,
                ["parameters"] = new JArray(Parameters.Select(p => new JObject
                {
                    ["name"] = p.Name,
                    ["type"] = p.WireType,
                    ["required"] = p.Required,
                    ["description"] = p.Description,
                })),
                ["schema"] = Schema(),
            };
        }

        // JSON Schema of the structured "parameters" object.
        JObject Schema()
        {
            var properties = new JObject();
            foreach (var p in Parameters)
                properties[p.Name] = p.Repeatable
                    ? new JObject
                    {
                        ["anyOf"] = new JArray(new JObject { ["type"] = "string" }, new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } }),
                        ["description"] = p.Description,
                    }
                    : new JObject { ["type"] = p.SchemaType, ["description"] = p.Description };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray(Parameters.Where(p => p.Required).Select(p => p.Name)),
                ["additionalProperties"] = false,
            };
        }
    }

    // What a CancellableHandler gets besides its parameters: the token (request timeout, job cancel or timeout, server
    // stop) and, for a detached job, its progress reporter (any thread; message null keeps the last one, percent null
    // when only a message is given). Progress is null outside a job.
    internal readonly struct CommandContext
    {
        public readonly CancellationToken Cancel;
        public readonly Action<string, int?> Progress;

        public CommandContext(CancellationToken cancel, Action<string, int?> progress = null)
        {
            Cancel = cancel;
            Progress = progress;
        }

        public static implicit operator CommandContext(CancellationToken cancel) => new CommandContext(cancel);
    }

    // Thrown by a handler to fail with a specific `error` and `errorDetails`, as the docs define per command.
    internal sealed class CommandFailure : Exception
    {
        public readonly string Error;
        // Sent as a synchronous failure reply's `result` (the unity CLI drops it, raw HTTP clients see it), or null. A
        // detached job keeps only error and errorDetails; for a test run, `test status --detail failures` has the rest.
        public JToken Result;

        public CommandFailure(string error, string details) : base(details)
        {
            Error = error;
        }

        public static CommandFailure InvalidUsage(string details) =>
            new CommandFailure("Parameter Validation Failed", "INVALID_COMMAND_ARGS: Bad Request. " + details);
    }

    internal sealed class ResolveResult
    {
        public CommandEntry Command;
        public string ListedGroup;
        public string NotFoundDetails;
        // What the caller typed as the command, echoed in a Command Not Found reply.
        public string Typed;
        public string[] RemainingTokens = Array.Empty<string>();
    }

    internal sealed class BindResult
    {
        public readonly JObject Values = new JObject();
        public readonly JArray Problems = new JArray();
        public bool Ok => Problems.Count == 0;
    }

    internal static class CommandRegistry
    {
        // The unity CLI adds "token" to some structured requests; no command declares it, so it is dropped.
        const string ExemptParameterName = "token";

        static readonly List<CommandEntry> s_Commands = new List<CommandEntry>();

        public static IReadOnlyList<CommandEntry> Commands => s_Commands;

        public static void Register(CommandEntry entry)
        {
            if (Find(entry.Group, entry.Name) != null)
                throw new InvalidOperationException($"Duplicate command '{entry.FullName}'");
            if (entry.Alias != null && FindAlias(entry.Alias) != null)
                throw new InvalidOperationException($"Duplicate alias '{entry.Alias}'");
            s_Commands.Add(entry);
            s_Commands.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
        }

        public static CommandEntry Find(string group, string name)
        {
            return s_Commands.FirstOrDefault(c => c.Group == group && c.Name == name);
        }

        static CommandEntry FindAlias(string alias) => s_Commands.FirstOrDefault(c => c.Alias == alias);

        static bool IsGroup(string token) => s_Commands.Any(c => InGroup(c, token));

        // Segment-aware: "build" covers "build" and "build.target", not "buildx".
        public static bool InGroup(CommandEntry command, string group)
        {
            return command.Group == group || command.Group.StartsWith(group + ".", StringComparison.Ordinal);
        }

        public static ResolveResult ResolveTokens(IReadOnlyList<string> tokens)
        {
            var first = tokens[0];
            if (IsGroup(first))
            {
                if (tokens.Count == 1)
                    return new ResolveResult { ListedGroup = first };
                var inGroup = Find(first, tokens[1]);
                if (inGroup != null)
                    return new ResolveResult { Command = inGroup, RemainingTokens = tokens.Skip(2).ToArray() };
                // A group name that is also an alias, followed by flags: `unity command console -- --tail 50`.
                var aliased = FindAlias(first);
                if (aliased != null && tokens[1].StartsWith("-", StringComparison.Ordinal))
                    return new ResolveResult { Command = aliased, RemainingTokens = tokens.Skip(1).ToArray() };
                var names = s_Commands.Where(c => c.Group == first).Select(c => c.Name);
                return new ResolveResult
                {
                    Typed = first + " " + tokens[1],
                    NotFoundDetails = $"Group '{first}' has no command '{tokens[1]}'. Commands: {string.Join(", ", names)}",
                };
            }

            var single = ResolveSingleToken(first, out var details);
            if (single != null)
                return new ResolveResult { Command = single, RemainingTokens = tokens.Skip(1).ToArray() };
            return new ResolveResult { Typed = first, NotFoundDetails = details };
        }

        // Structured "command": "<group> <name>", or any single-token form.
        public static CommandEntry ResolveStructured(string command, out string details)
        {
            details = null;
            var space = command.IndexOf(' ');
            if (space > 0)
            {
                var found = Find(command.Substring(0, space), command.Substring(space + 1));
                if (found == null)
                    details = $"Unknown command '{command}'";
                return found;
            }
            return ResolveSingleToken(command, out details);
        }

        // One token, in argv or structured: an alias, then the documented dotted and unique-name forms, then
        // "<group>_<name>" (`unity mcp` sends whatever tool name it was given), refused when more than one split
        // names a command.
        static CommandEntry ResolveSingleToken(string command, out string details)
        {
            var aliased = FindAlias(command);
            if (aliased != null)
            {
                details = null;
                return aliased;
            }
            var resolved = ResolveSingleName(command, out details);
            if (resolved != null)
                return resolved;

            var splits = new List<CommandEntry>();
            for (var i = command.IndexOf('_'); i > 0; i = command.IndexOf('_', i + 1))
            {
                var found = Find(command.Substring(0, i), command.Substring(i + 1));
                if (found != null)
                    splits.Add(found);
            }
            if (splits.Count == 1)
            {
                details = null;
                return splits[0];
            }
            if (splits.Count > 1)
                details = $"Command name '{command}' is ambiguous: {string.Join(", ", splits.Select(c => c.FullName))}";
            return null;
        }

        static CommandEntry ResolveSingleName(string token, out string details)
        {
            details = null;
            var dot = token.LastIndexOf('.');
            if (dot > 0)
            {
                var dotted = Find(token.Substring(0, dot), token.Substring(dot + 1));
                if (dotted != null)
                    return dotted;
            }

            var byName = s_Commands.Where(c => c.Name == token).ToList();
            if (byName.Count == 1)
                return byName[0];
            details = byName.Count > 1
                ? $"Command name '{token}' is ambiguous: {string.Join(", ", byName.Select(c => c.FullName))}"
                : $"Unknown command '{token}'";
            return null;
        }

        public static JObject ListGroup(string group)
        {
            var prefix = group + ".";
            var subgroups = s_Commands
                .Select(c => c.Group)
                .Where(g => g.StartsWith(prefix, StringComparison.Ordinal))
                .Select(g => prefix + g.Substring(prefix.Length).Split('.')[0])
                .Distinct()
                .OrderBy(g => g, StringComparer.Ordinal);
            var commands = s_Commands.Where(c => c.Group == group).Select(c => new JObject
            {
                ["command"] = c.FullName,
                ["name"] = c.Name,
                ["description"] = c.Description,
            });
            return new JObject
            {
                ["group"] = group,
                ["subgroups"] = new JArray(subgroups),
                ["commands"] = new JArray(commands),
            };
        }

        // Grammar from the public docs: --key value, --key=value, bare --key (bool only), "--" ends flags,
        // a follower is a value unless it starts with "--", positionals fill required then optional slots.
        public static BindResult BindArgv(CommandEntry command, IReadOnlyList<string> tokens)
        {
            var result = new BindResult();
            var positionals = new List<string>();
            var flagsEnded = false;

            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (flagsEnded || !token.StartsWith("--", StringComparison.Ordinal))
                {
                    if (!flagsEnded && token.Length > 1 && token[0] == '-')
                    {
                        result.Problems.Add(Problem("singleDash", token));
                        continue;
                    }
                    positionals.Add(token);
                    continue;
                }
                if (token == "--")
                {
                    flagsEnded = true;
                    continue;
                }

                var body = token.Substring(2);
                string name = body, value = null;
                var equals = body.IndexOf('=');
                if (equals >= 0)
                {
                    name = body.Substring(0, equals);
                    value = body.Substring(equals + 1);
                }
                if (name.Length == 0)
                {
                    result.Problems.Add(Problem("emptyName", token));
                    continue;
                }
                if (value != null && value.Length == 0)
                {
                    result.Problems.Add(Problem("emptyValue", name));
                    continue;
                }

                var parameter = FindParameter(command, name);
                if (value == null && i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
                    value = tokens[++i];
                if (parameter == null)
                {
                    result.Problems.Add(Problem("unknownName", name));
                    continue;
                }
                if (result.Values.ContainsKey(parameter.Name) && !parameter.Repeatable)
                {
                    result.Problems.Add(Problem("duplicate", parameter.Name));
                    continue;
                }
                if (value == null)
                {
                    if (parameter.IsBool)
                        result.Values[parameter.Name] = true;
                    else
                        result.Problems.Add(Problem("emptyValue", parameter.Name));
                    continue;
                }
                StoreValue(result, parameter, value);
            }

            var freeSlots = command.Parameters.Where(p => p.Required)
                .Concat(command.Parameters.Where(p => !p.Required))
                .Where(p => !result.Values.ContainsKey(p.Name))
                .ToList();
            for (var i = 0; i < positionals.Count; i++)
            {
                if (i >= freeSlots.Count)
                {
                    var problem = Problem("excessPositional", null);
                    problem["token"] = positionals[i];
                    problem["capacity"] = freeSlots.Count;
                    problem["given"] = positionals.Count;
                    result.Problems.Add(problem);
                    break;
                }
                StoreValue(result, freeSlots[i], positionals[i]);
            }

            AddMissingRequired(command, result);
            return result;
        }

        public static BindResult BindStructured(CommandEntry command, JObject parameters)
        {
            var result = new BindResult();
            if (parameters != null)
            {
                foreach (var property in parameters.Properties())
                {
                    if (property.Name == ExemptParameterName)
                        continue;
                    var parameter = FindParameter(command, property.Name);
                    if (parameter == null)
                    {
                        result.Problems.Add(Problem("unknownName", property.Name));
                        continue;
                    }
                    // A null is a value not given, so a required parameter sent as null is reported missing.
                    if (property.Value.Type == JTokenType.Null)
                        continue;
                    if (result.Values.ContainsKey(parameter.Name))
                    {
                        result.Problems.Add(Problem("duplicate", parameter.Name));
                        continue;
                    }
                    if (parameter.Repeatable && !IsValidRepeatableValue(property.Value))
                    {
                        var mismatch = Problem("typeMismatch", parameter.Name);
                        mismatch["expectedType"] = "String[]";
                        result.Problems.Add(mismatch);
                        continue;
                    }
                    // The same rule argv applies: a boolean or integer parameter takes only a value that parses as one.
                    if ((parameter.IsBool && !IsBoolValue(property.Value)) || (parameter.IsInteger && !IsIntegerValue(property.Value)))
                    {
                        var mismatch = Problem("typeMismatch", parameter.Name);
                        mismatch["expectedType"] = parameter.WireType;
                        result.Problems.Add(mismatch);
                        continue;
                    }
                    result.Values[parameter.Name] = property.Value;
                }
            }
            AddMissingRequired(command, result);
            return result;
        }

        // The unity CLI and its skill docs spell hyphenated parameters with underscores ("since_session", "timeout_ms").
        static CommandParameter FindParameter(CommandEntry command, string name)
        {
            return command.Parameters.FirstOrDefault(p => p.Name == name)
                ?? command.Parameters.FirstOrDefault(p => p.Name == name.Replace('_', '-'));
        }

        static bool IsValidRepeatableValue(JToken value)
        {
            if (value.Type == JTokenType.Null || value.Type == JTokenType.String)
                return true;
            if (value is JArray items)
                return items.All(item => item.Type == JTokenType.String);
            return false;
        }

        // Exactly what ReadBool and ReadInteger accept, so a value that binds is a value the handler can read.
        static bool IsBoolValue(JToken value) => value is JValue && bool.TryParse(value.ToString(), out _);

        static bool IsIntegerValue(JToken value) => value is JValue && long.TryParse(value.ToString(), out _);

        static void StoreValue(BindResult result, CommandParameter parameter, string value)
        {
            if (parameter.IsInteger && !long.TryParse(value, out _))
            {
                var mismatch = Problem("typeMismatch", parameter.Name);
                mismatch["expectedType"] = parameter.WireType;
                result.Problems.Add(mismatch);
                return;
            }
            JToken bound;
            if (!parameter.IsBool)
                bound = value;
            else if (bool.TryParse(value, out var flag))
                bound = flag;
            else
            {
                var problem = Problem("typeMismatch", parameter.Name);
                problem["expectedType"] = "Boolean";
                result.Problems.Add(problem);
                return;
            }
            if (parameter.Repeatable)
            {
                ((JArray)(result.Values[parameter.Name] ??= new JArray())).Add(bound);
                return;
            }
            result.Values[parameter.Name] = bound;
        }

        static void AddMissingRequired(CommandEntry command, BindResult result)
        {
            foreach (var parameter in command.Parameters.Where(p => p.Required && !result.Values.ContainsKey(p.Name)))
                result.Problems.Add(Problem("missingRequired", parameter.Name));
        }

        static JObject Problem(string kind, string name)
        {
            var problem = new JObject { ["kind"] = kind };
            if (name != null)
                problem["name"] = name;
            return problem;
        }

        // Handlers read bound values with these: argv binds strings and booleans, structured requests pass JSON as sent.
        public static long ReadInteger(JObject parameters, string name, long fallback)
        {
            var token = parameters[name];
            if (token == null || token.Type == JTokenType.Null)
                return fallback;
            if (!long.TryParse(token.ToString(), out var value))
                throw CommandFailure.InvalidUsage($"{name} must be an integer.");
            return value;
        }

        public static long ReadInteger(JObject parameters, string name, long fallback, long min, long max)
        {
            var value = ReadInteger(parameters, name, fallback);
            if (value < min || value > max)
                throw CommandFailure.InvalidUsage($"{name} must be between {min} and {max}.");
            return value;
        }

        public static bool ReadBool(JObject parameters, string name, bool fallback)
        {
            var token = parameters[name];
            if (token == null || token.Type == JTokenType.Null)
                return fallback;
            if (!bool.TryParse(token.ToString(), out var value))
                throw CommandFailure.InvalidUsage($"{name} must be true or false.");
            return value;
        }

        // A Repeatable parameter's values in the order sent: argv binds a JArray of strings, structured requests a string
        // or an array of strings. Each value goes through `split` (the command's separators), except one that holds a
        // JSON array of strings: its elements are taken as they are, which is how a value containing a separator is sent.
        public static List<string> ReadStrings(JObject parameters, string name, Func<string, IEnumerable<string>> split = null)
        {
            var values = new List<string>();
            var token = parameters[name];
            if (token == null || token.Type == JTokenType.Null)
                return values;
            foreach (var item in token is JArray items ? (IEnumerable<JToken>)items : new[] { token })
            {
                if (item.Type != JTokenType.String)
                    throw CommandFailure.InvalidUsage($"{name} must be a string or an array of strings.");
                var text = (string)item;
                if (text.TrimStart().StartsWith("[", StringComparison.Ordinal) && TryParseStringArray(text, out var exact))
                    values.AddRange(exact);
                else if (split != null)
                    values.AddRange(split(text));
                else
                    values.Add(text);
            }
            return values;
        }

        static bool TryParseStringArray(string text, out List<string> values)
        {
            values = null;
            try
            {
                if (!(JToken.Parse(text) is JArray array) || array.Any(item => item.Type != JTokenType.String))
                    return false;
                values = array.Select(item => (string)item).ToList();
                return true;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }
    }
}
