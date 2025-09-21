
using System.Collections.Immutable;

namespace Serde.CmdLine;

internal record Command(
    int? FieldIndex,
    string Name,
    ImmutableArray<Option> Options,
    ImmutableArray<Command> SubCommands,
    ImmutableArray<Parameter> Parameters,
    ImmutableArray<CommandGroup> CommandGroups);

internal record Option(int FieldIndex, string[] Names);

internal record CommandGroup(
    int FieldIndex,
    string Name,
    ImmutableArray<Command> Commands);

internal record Parameter(int FieldIndex, int Ordinal, string Name);