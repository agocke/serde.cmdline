using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;

namespace Serde.CmdLine;

internal sealed partial class Deserializer(string[] args, bool handleHelp) : IDeserializer
{
    private int _argIndex = 0;
    private int _paramIndex = 0;
    private bool _throwOnMissing = true;
    private readonly List<ISerdeInfo> _helpInfos = new();
    private Command? _rootCommand;

    public IReadOnlyList<ISerdeInfo> HelpInfos => _helpInfos;

    int ITypeDeserializer.TryReadIndex(ISerdeInfo serdeInfo, out string? errorName)
    {
        if (_argIndex == args.Length)
        {
            errorName = null;
            return ITypeDeserializer.EndOfType;
        }

        var arg = args[_argIndex];
        while (handleHelp && arg is "-h" or "--help")
        {
            _argIndex++;
            _helpInfos.Add(serdeInfo);
            if (_argIndex == args.Length)
            {
                errorName = null;
                return ITypeDeserializer.EndOfType;
            }
            arg = args[_argIndex];
        }

        if (_rootCommand is not { } rootCmd)
        {
            throw new InvalidOperationException("ReadType must be called before TryReadIndex.");
        }

        foreach (var option in rootCmd.Options)
        {
            if (option.Names.Contains(arg))
            {
                _argIndex++;
                errorName = null;
                return option.FieldIndex;
            }
        }

        foreach (var cmd in rootCmd.SubCommands)
        {
            if (cmd.Name == arg)
            {
                _argIndex++;
                errorName = null;
                return cmd.FieldIndex ?? throw new InvalidOperationException("Subcommand field index cannot be null.");
            }
        }

        foreach (var cmdGroup in rootCmd.CommandGroups)
        {
            foreach (var cmd in cmdGroup.Commands)
            {
                if (cmd.Name == arg)
                {
                    _argIndex++;
                    errorName = null;
                    return cmdGroup.FieldIndex;
                }
            }
        }

        foreach (var param in rootCmd.Parameters)
        {
            if (!arg.StartsWith('-') && _paramIndex == param.Ordinal)
            {
                _paramIndex++;
                errorName = null;
                return param.FieldIndex;
            }
        }

        if (_throwOnMissing)
        {
            throw new ArgumentSyntaxException($"Unexpected argument: '{arg}'");
        }
        else
        {
            errorName = arg;
            return ITypeDeserializer.IndexNotFound;
        }
    }

    public bool ReadBool()
    {
        // Flags are a little tricky. They can be specified as --flag or '--flag true' or '--flag false'.
        // There's no way to know for sure whether the current argument is a flag or a value, so we'll
        // try to parse it as a bool. If it fails, we'll assume it's a flag and return true.
        if (_argIndex == args.Length || !bool.TryParse(args[_argIndex], out bool value))
        {
            return true;
        }
        _argIndex++;
        return value;
    }

    public string ReadString() => args[_argIndex++];

    public T ReadNullableRef<T>(IDeserialize<T> d)
        where T : class
    {
        // Treat all nullable values as just being optional. Since we got here we must have a value
        // in hand.
        return d.Deserialize(this);
    }

    public char ReadChar() => throw new NotImplementedException();
    public byte ReadU8() => throw new NotImplementedException();
    public ushort ReadU16() => throw new NotImplementedException();
    public uint ReadU32() => throw new NotImplementedException();
    public ulong ReadU64() => throw new NotImplementedException();
    public sbyte ReadI8() => throw new NotImplementedException();
    public short ReadI16() => throw new NotImplementedException();
    public int ReadI32() => throw new NotImplementedException();
    public long ReadI64() => throw new NotImplementedException();
    public float ReadF32() => throw new NotImplementedException();
    public double ReadF64() => throw new NotImplementedException();
    public decimal ReadDecimal() => throw new NotImplementedException();
    public DateTime ReadDateTime() => throw new NotImplementedException();
    public void ReadBytes(IBufferWriter<byte> writer) => throw new NotImplementedException();

    public ITypeDeserializer ReadType(ISerdeInfo typeInfo)
    {
        _rootCommand = MakeCommand(typeInfo);
        return this;
    }

    private static Command MakeCommand(ISerdeInfo serdeInfo)
    {
        var options = new List<Option>();
        var commands = new List<Command>();
        var commandGroups = new List<CommandGroup>();
        var parameters = new List<Parameter>();
        for (int fieldIndex = 0; fieldIndex < serdeInfo.FieldCount; fieldIndex++)
        {
            IList<CustomAttributeData> attrs = serdeInfo.GetFieldAttributes(fieldIndex);
            foreach (var attr in attrs)
            {
                if (attr is
                    {
                        AttributeType: { Name: nameof(CommandOptionAttribute) },
                        ConstructorArguments: [{ Value: string flagNames }]
                    })
                {
                    var flagNamesArray = flagNames.Split('|');
                    options.Add(new Option(fieldIndex, flagNamesArray));
                    break;
                }

                if (attr is
                    {
                        AttributeType: { Name: nameof(CommandAttribute) },
                        ConstructorArguments: [{ Value: string commandName }]
                    })
                {
                    commands.Add(new Command(
                        fieldIndex,
                        commandName,
                        Options: ImmutableArray<Option>.Empty,
                        SubCommands: ImmutableArray<Command>.Empty,
                        Parameters: ImmutableArray<Parameter>.Empty,
                        CommandGroups: ImmutableArray<CommandGroup>.Empty));
                    break;
                }

                if (attr is
                    {
                        AttributeType: { Name: nameof(CommandGroupAttribute) },
                        ConstructorArguments: [{ Value: string groupName }]
                    })
                {
                    // If the field is a command group, check to see if any of the nested commands match
                    // the argument. If so, mark this field as a match.
#pragma warning disable SerdeExperimentalFieldInfo // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                    var fieldInfo = serdeInfo.GetFieldInfo(fieldIndex);
                    if (fieldInfo.Kind == InfoKind.Nullable)
                    {
                        // Unwrap nullable if present
                        fieldInfo = fieldInfo.GetFieldInfo(0);
                    }
#pragma warning restore SerdeExperimentalFieldInfo // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                    commandGroups.Add(MakeCommandGroup(fieldIndex, fieldInfo));
                    break;
                }

                if (attr is
                    {
                        AttributeType: { Name: nameof(CommandParameterAttribute) },
                        ConstructorArguments: [{ Value: int paramIndex }, { Value: string paramName }]
                    })
                {
                    parameters.Add(new Parameter(fieldIndex, paramIndex, paramName));
                    break;
                }

                throw new InvalidOperationException($"Field {serdeInfo.GetFieldStringName(fieldIndex)} is missing a required attribute.");
            }
        }

        string? cmdName = null;
        foreach (var attr in serdeInfo.Attributes)
        {
            if (attr is
                {
                    AttributeType: { Name: nameof(CommandAttribute) },
                    ConstructorArguments: [{ Value: string commandName }]
                })
            {
                cmdName = commandName;
                break;
            }
        }

        return new Command(
                FieldIndex: null,
                Name: cmdName ?? serdeInfo.Name,
                Options: options.ToImmutableArray(),
                SubCommands: commands.ToImmutableArray(),
                Parameters: parameters.ToImmutableArray(),
                CommandGroups: commandGroups.ToImmutableArray());
    }

    private static CommandGroup MakeCommandGroup(int fieldIndex, ISerdeInfo fieldInfo)
    {
        var bldr = ImmutableArray.CreateBuilder<Command>();
        for (int i = 0; i < fieldInfo.FieldCount; i++)
        {
#pragma warning disable SerdeExperimentalFieldInfo // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            var nestedInfo = fieldInfo.GetFieldInfo(i);
#pragma warning restore SerdeExperimentalFieldInfo // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            var cmd = MakeCommand(nestedInfo);
            bldr.Add(cmd);
        }
        return new CommandGroup(fieldIndex, fieldInfo.Name, bldr.ToImmutable());
    }

    public void Dispose() { }
}