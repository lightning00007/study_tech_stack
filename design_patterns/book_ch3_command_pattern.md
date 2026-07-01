# Chapter 3: The Command Pattern — Encapsulating Actions as Objects

> **Behavioral Pattern · GoF Classic · Foundation of Undo/Redo and Task Queues**
> *"Encapsulate a request as an object, thereby letting you parameterize clients with different requests, queue or log requests, and support undoable operations."*
> — Gang of Four

---

## Table of Contents

1. [Introduction — The Problem of Hard-Coded Actions](#1-introduction)
2. [Classic GoF Command — Step by Step](#2-classic-gof)
3. [Real-World Example: Text Editor with Undo/Redo](#3-text-editor-undo-redo)
4. [Real-World Example: Smart Home Automation](#4-smart-home)
5. [Command Queuing and Scheduling](#5-command-queuing)
6. [Transactional Commands with Rollback](#6-transactional-commands)
7. [Command vs. Mediator vs. Strategy — When to Use Each](#7-command-vs-others)
8. [Summary](#8-summary)

---

## 1. Introduction — The Problem of Hard-Coded Actions

Imagine you are building a text editor. You have `Copy`, `Paste`, `Bold`, `Undo`, `Redo` operations. These operations can be triggered from:

- A menu item
- A toolbar button
- A keyboard shortcut
- A context menu

Without the Command pattern, every UI element must know HOW to perform the action:

```csharp
// ❌ NAIVE: Button directly calls the editor
public class BoldButton
{
    private readonly TextEditor _editor;

    public void Click()
    {
        // Button is tightly coupled to TextEditor
        _editor.ApplyBoldFormatting();
    }
}

public class BoldMenuItem
{
    private readonly TextEditor _editor;

    public void OnClick()
    {
        // Same logic duplicated — DRY violation
        _editor.ApplyBoldFormatting();
    }
}
```

Problems:
1. **Logic duplication**: Every trigger duplicates the same call.
2. **No undo support**: How does a button "undo" itself?
3. **No queuing**: You cannot queue or delay the action.
4. **No logging**: You cannot record what actions were taken.

### The Command Solution

Turn each operation into an **object**. The object knows how to **execute** itself and optionally how to **undo** itself.

---

## 2. Classic GoF Command — Step by Step

### 2.1 The Interfaces

```csharp
// The Command interface — every command must implement this
public interface ICommand
{
    void Execute();
    void Undo();  // Optional but crucial for undo/redo
}

// The Invoker — holds and runs commands (toolbar, menu, etc.)
public class CommandInvoker
{
    private readonly Stack<ICommand> _history = new();
    private readonly Stack<ICommand> _redoStack = new();

    public void ExecuteCommand(ICommand command)
    {
        command.Execute();
        _history.Push(command);
        _redoStack.Clear(); // New action clears redo history
    }

    public void Undo()
    {
        if (_history.Count == 0) return;
        var command = _history.Pop();
        command.Undo();
        _redoStack.Push(command);
    }

    public void Redo()
    {
        if (_redoStack.Count == 0) return;
        var command = _redoStack.Pop();
        command.Execute();
        _history.Push(command);
    }
}
```

### 2.2 The Receiver

```csharp
// The Receiver — the object that actually knows how to do the work
// The Command delegates work to the Receiver
public class Light
{
    public bool IsOn { get; private set; } = false;
    public int Brightness { get; private set; } = 0;

    public void TurnOn(int brightness = 100)
    {
        IsOn = true;
        Brightness = brightness;
        Console.WriteLine($"💡 Light ON at {brightness}% brightness");
    }

    public void TurnOff()
    {
        IsOn = false;
        Brightness = 0;
        Console.WriteLine("💡 Light OFF");
    }

    public void SetBrightness(int brightness)
    {
        Brightness = brightness;
        Console.WriteLine($"💡 Brightness set to {brightness}%");
    }
}
```

### 2.3 Concrete Commands

```csharp
// Command: Turn On Light
public class TurnOnLightCommand : ICommand
{
    private readonly Light _light;
    private readonly int _brightness;
    private int _previousBrightness; // Store state for undo
    private bool _wasOn;

    public TurnOnLightCommand(Light light, int brightness = 100)
    {
        _light      = light;
        _brightness = brightness;
    }

    public void Execute()
    {
        // Capture state BEFORE executing so we can undo
        _wasOn             = _light.IsOn;
        _previousBrightness = _light.Brightness;

        _light.TurnOn(_brightness);
    }

    public void Undo()
    {
        // Restore exact previous state
        if (_wasOn)
            _light.TurnOn(_previousBrightness);
        else
            _light.TurnOff();
    }
}

// Command: Dim Light
public class DimLightCommand : ICommand
{
    private readonly Light _light;
    private readonly int _newBrightness;
    private int _previousBrightness;

    public DimLightCommand(Light light, int brightness)
    {
        _light         = light;
        _newBrightness = brightness;
    }

    public void Execute()
    {
        _previousBrightness = _light.Brightness;
        _light.SetBrightness(_newBrightness);
    }

    public void Undo()
    {
        _light.SetBrightness(_previousBrightness);
    }
}

// Macro Command — a command that wraps multiple commands (Composite pattern integration)
public class MacroCommand : ICommand
{
    private readonly List<ICommand> _commands;

    public MacroCommand(params ICommand[] commands)
        => _commands = new List<ICommand>(commands);

    public void Execute()
    {
        foreach (var command in _commands)
            command.Execute();
    }

    public void Undo()
    {
        // Undo in reverse order
        foreach (var command in Enumerable.Reverse(_commands))
            command.Undo();
    }
}
```

### 2.4 Usage

```csharp
var light   = new Light();
var invoker = new CommandInvoker();

// Execute commands
invoker.ExecuteCommand(new TurnOnLightCommand(light, brightness: 80));
// 💡 Light ON at 80% brightness

invoker.ExecuteCommand(new DimLightCommand(light, brightness: 40));
// 💡 Brightness set to 40%

// Undo the dimming
invoker.Undo();
// 💡 Brightness set to 80%

// Undo turning on (restores off state)
invoker.Undo();
// 💡 Light OFF

// Redo everything
invoker.Redo();
// 💡 Light ON at 80% brightness

// Macro command — one command triggers multiple actions
var eveningModeCommand = new MacroCommand(
    new TurnOnLightCommand(light, 30),
    // other commands like closing blinds, turning on TV, etc.
);
invoker.ExecuteCommand(eveningModeCommand);
invoker.Undo(); // Undoes ALL macro commands in reverse
```

---

## 3. Real-World Example: Text Editor with Undo/Redo

```csharp
// The Receiver
public class Document
{
    private readonly StringBuilder _text = new();
    public string Text => _text.ToString();

    public void Insert(int position, string text)
    {
        _text.Insert(position, text);
        Console.WriteLine($"Document: \"{_text}\"");
    }

    public void Delete(int position, int length)
    {
        _text.Remove(position, length);
        Console.WriteLine($"Document: \"{_text}\"");
    }
}

// Command: Insert Text
public class InsertTextCommand : ICommand
{
    private readonly Document _document;
    private readonly int _position;
    private readonly string _text;

    public InsertTextCommand(Document document, int position, string text)
    {
        _document = document;
        _position = position;
        _text     = text;
    }

    public void Execute() => _document.Insert(_position, _text);
    public void Undo()    => _document.Delete(_position, _text.Length);
}

// Command: Delete Text
public class DeleteTextCommand : ICommand
{
    private readonly Document _document;
    private readonly int _position;
    private readonly int _length;
    private string _deletedText = string.Empty; // Must save what was deleted for undo!

    public DeleteTextCommand(Document document, int position, int length)
    {
        _document = document;
        _position = position;
        _length   = length;
    }

    public void Execute()
    {
        // IMPORTANT: capture text before deletion for undo
        _deletedText = _document.Text.Substring(_position, _length);
        _document.Delete(_position, _length);
    }

    public void Undo() => _document.Insert(_position, _deletedText);
}

// Usage
var doc     = new Document();
var history = new CommandInvoker();

history.ExecuteCommand(new InsertTextCommand(doc, 0, "Hello"));
// Document: "Hello"
history.ExecuteCommand(new InsertTextCommand(doc, 5, ", World!"));
// Document: "Hello, World!"
history.ExecuteCommand(new DeleteTextCommand(doc, 5, 8)); // Delete ", World"
// Document: "Hello!"

history.Undo(); // Restore deleted text
// Document: "Hello, World!"
history.Undo(); // Remove ", World!"
// Document: "Hello"
```

---

## 4. Real-World Example: Smart Home Automation

```csharp
// Multiple receivers
public class AirConditioner
{
    public bool IsOn { get; private set; }
    public int Temperature { get; private set; } = 22;

    public void TurnOn(int temperature)
    {
        IsOn = true;
        Temperature = temperature;
        Console.WriteLine($"❄️  AC ON at {temperature}°C");
    }

    public void TurnOff()
    {
        IsOn = false;
        Console.WriteLine("❄️  AC OFF");
    }
}

public class MusicPlayer
{
    public bool IsPlaying { get; private set; }
    public string? CurrentPlaylist { get; private set; }

    public void Play(string playlist)
    {
        IsPlaying = true;
        CurrentPlaylist = playlist;
        Console.WriteLine($"🎵 Playing: {playlist}");
    }

    public void Stop()
    {
        IsPlaying = false;
        Console.WriteLine("🎵 Music stopped");
    }
}

// Commands
public class TurnOnAcCommand : ICommand
{
    private readonly AirConditioner _ac;
    private readonly int _temperature;
    private bool _wasOn;
    private int _prevTemp;

    public TurnOnAcCommand(AirConditioner ac, int temperature)
    {
        _ac = ac;
        _temperature = temperature;
    }

    public void Execute()
    {
        _wasOn   = _ac.IsOn;
        _prevTemp = _ac.Temperature;
        _ac.TurnOn(_temperature);
    }

    public void Undo()
    {
        if (_wasOn) _ac.TurnOn(_prevTemp);
        else        _ac.TurnOff();
    }
}

public class PlayMusicCommand : ICommand
{
    private readonly MusicPlayer _player;
    private readonly string _playlist;
    private bool _wasPlaying;
    private string? _prevPlaylist;

    public PlayMusicCommand(MusicPlayer player, string playlist)
    {
        _player   = player;
        _playlist = playlist;
    }

    public void Execute()
    {
        _wasPlaying  = _player.IsPlaying;
        _prevPlaylist = _player.CurrentPlaylist;
        _player.Play(_playlist);
    }

    public void Undo()
    {
        if (_wasPlaying && _prevPlaylist is not null)
            _player.Play(_prevPlaylist);
        else
            _player.Stop();
    }
}

// "Movie Night" scene — a macro of multiple device commands
var ac     = new AirConditioner();
var music  = new MusicPlayer();
var invoker = new CommandInvoker();

var movieNightScene = new MacroCommand(
    new TurnOnAcCommand(ac, temperature: 20),
    new PlayMusicCommand(music, "Cinematic Ambience")
    // + dim lights, close blinds, etc.
);

invoker.ExecuteCommand(movieNightScene);
// ❄️  AC ON at 20°C
// 🎵 Playing: Cinematic Ambience

Console.WriteLine("\n--- Undoing Movie Night scene ---");
invoker.Undo();
// 🎵 Music stopped
// ❄️  AC OFF
```

---

## 5. Command Queuing and Scheduling

One of the GoF's stated benefits is that commands can be **queued and executed later**:

```csharp
// A delayed/queued command execution system
public class CommandQueue
{
    private readonly Queue<ICommand> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;

    public void Enqueue(ICommand command)
    {
        lock (_pending)
            _pending.Enqueue(command);
    }

    public void StartProcessing()
    {
        _processingTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                ICommand? command = null;
                lock (_pending)
                {
                    if (_pending.Count > 0)
                        command = _pending.Dequeue();
                }

                if (command is not null)
                {
                    Console.WriteLine($"Executing queued command: {command.GetType().Name}");
                    command.Execute();
                }
                else
                {
                    await Task.Delay(50, _cts.Token); // Poll every 50ms
                }
            }
        }, _cts.Token);
    }

    public void Stop() => _cts.Cancel();
}

// Usage
var light = new Light();
var queue = new CommandQueue();
queue.StartProcessing();

// Enqueue commands — they execute in the background
queue.Enqueue(new TurnOnLightCommand(light, 100));
queue.Enqueue(new DimLightCommand(light, 50));
queue.Enqueue(new DimLightCommand(light, 20));

await Task.Delay(500); // Let the queue process
queue.Stop();
```

---

## 6. Transactional Commands with Rollback

In enterprise applications, you often need commands to be **transactional** — either all succeed or all roll back.

```csharp
public class TransactionalCommandProcessor
{
    private readonly List<ICommand> _executedCommands = new();

    public bool ExecuteAll(IEnumerable<ICommand> commands)
    {
        _executedCommands.Clear();
        foreach (var command in commands)
        {
            try
            {
                command.Execute();
                _executedCommands.Add(command);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Command failed: {ex.Message}. Rolling back...");
                RollbackAll();
                return false;
            }
        }
        return true;
    }

    private void RollbackAll()
    {
        // Undo in reverse order
        foreach (var command in Enumerable.Reverse(_executedCommands))
        {
            try { command.Undo(); }
            catch (Exception ex) { Console.WriteLine($"Rollback error: {ex.Message}"); }
        }
        _executedCommands.Clear();
    }
}

// Commands that simulate a bank transfer
public class DebitAccountCommand : ICommand
{
    private readonly BankAccount _account;
    private readonly decimal _amount;

    public DebitAccountCommand(BankAccount account, decimal amount)
    {
        _account = account;
        _amount  = amount;
    }

    public void Execute()
    {
        if (_account.Balance < _amount)
            throw new InsufficientFundsException($"Need {_amount}, have {_account.Balance}");
        _account.Balance -= _amount;
        Console.WriteLine($"Debited ${_amount}. Balance: ${_account.Balance}");
    }

    public void Undo()
    {
        _account.Balance += _amount;
        Console.WriteLine($"Rolled back debit ${_amount}. Balance: ${_account.Balance}");
    }
}

public class CreditAccountCommand : ICommand
{
    private readonly BankAccount _account;
    private readonly decimal _amount;

    public CreditAccountCommand(BankAccount account, decimal amount)
    {
        _account = account;
        _amount  = amount;
    }

    public void Execute()
    {
        _account.Balance += _amount;
        Console.WriteLine($"Credited ${_amount}. Balance: ${_account.Balance}");
    }

    public void Undo()
    {
        _account.Balance -= _amount;
        Console.WriteLine($"Rolled back credit ${_amount}. Balance: ${_account.Balance}");
    }
}

// Usage
var alice = new BankAccount { Id = "Alice", Balance = 100m };
var bob   = new BankAccount { Id = "Bob",   Balance = 50m  };

var processor = new TransactionalCommandProcessor();

// Transfer $75 from Alice to Bob
var transferCommands = new ICommand[]
{
    new DebitAccountCommand(alice, 75m),
    new CreditAccountCommand(bob, 75m)
};

bool success = processor.ExecuteAll(transferCommands);
// Debited $75. Balance: $25
// Credited $75. Balance: $125
Console.WriteLine($"Transfer {(success ? "succeeded" : "failed")}");

// Now try an impossible transfer — will roll back
var failingTransfer = new ICommand[]
{
    new DebitAccountCommand(alice, 500m), // Will fail — insufficient funds
    new CreditAccountCommand(bob, 500m)
};

processor.ExecuteAll(failingTransfer);
// Command failed: Need 500, have 25. Rolling back...
// (no undo needed since Debit failed and Credit never ran)
```

---

## 7. Command vs. Mediator vs. Strategy — When to Use Each

These three patterns are frequently confused. Here is the definitive guide:

| Dimension | Command | Mediator | Strategy |
|---|---|---|---|
| **Core idea** | Encapsulate an action as an object | Central hub for coordination | Encapsulate an algorithm as an object |
| **Primary goal** | Undo/redo, queuing, logging of actions | Decouple many communicating objects | Swap algorithms at runtime |
| **Who calls what** | Invoker → Command → Receiver | Component → Mediator → Component | Context → Strategy |
| **State stored?** | YES — stores pre-execution state for undo | Minimal | No |
| **Return value** | Optional (usually void) | Often returns a result | Always returns a result |
| **Typical use** | Text editors, game commands, task queues | CQRS, UI dialogs, workflow | Sorting, payment methods, compression |

### Quick Examples

```csharp
// COMMAND: An action captured as an object, reversible
public class SendEmailCommand : ICommand
{
    public void Execute() => emailService.Send(...);
    public void Undo()    => emailService.MarkAsUnsent(...); // or queue for deletion
}

// MEDIATOR: Orchestrates coordination between objects
public class SendEmailHandler : IRequestHandler<SendEmailCommand, bool>
{
    public async Task<bool> Handle(SendEmailCommand req, CancellationToken ct)
        => await emailService.SendAsync(req.To, req.Subject, req.Body, ct);
}

// STRATEGY: A swappable algorithm
public interface IEmailFormatterStrategy
{
    string Format(Email email);
}

public class HtmlEmailFormatter : IEmailFormatterStrategy { ... }
public class PlainTextEmailFormatter : IEmailFormatterStrategy { ... }
```

---

## 8. Summary

- The **Command pattern** turns actions into first-class objects, enabling undo/redo, queuing, logging, and transactional rollback.
- The **key insight** is capturing "before state" in Execute() so Undo() can restore it perfectly.
- **Macro Commands** (Composite + Command) let you group multiple actions into one undoable unit.
- **Transactional Command Processor** wraps multiple commands in an all-or-nothing transaction.
- Use Command when you need **reversibility** or **deferred execution**. Use Mediator when you need **decoupled communication**. Use Strategy when you need **swappable algorithms**.

---

*Next Chapter →* [Chapter 4: The Chain of Responsibility Pattern](book_ch4_chain_of_responsibility.md)
*Previous Chapter →* [Chapter 2: The Observer Pattern](book_ch2_observer_pattern.md)
