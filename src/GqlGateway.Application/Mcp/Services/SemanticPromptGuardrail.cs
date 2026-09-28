namespace GqlGateway.Application.Mcp.Services;

using System;
using System.Text;
using System.Text.RegularExpressions;
using GqlGateway.Application.Mcp.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// Semantic Prompt-Injection and Jailbreak Guardrail engine (inspired by NeMo Guardrails / Llama Guard heuristics).
/// Analyzes tool arguments and prompt strings for prompt injection, jailbreak personas, delimiter escapes,
/// and obfuscated malicious instructions prior to tool execution.
/// </summary>
public sealed class SemanticPromptGuardrail : ISemanticPromptGuardrail
{
    private readonly ILogger<SemanticPromptGuardrail>? _logger;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    // 1. Direct Instruction Overrides
    private static readonly Regex DirectInstructionOverrideRegex = new(
        @"(?:ignore|disregard|forget|bypass|override)\s+(?:all\s+)?(?:previous|prior|system|all)\s+(?:instructions|directives|prompts|rules|guidelines|guardrails)|(?:output|print|reveal|show|dump)\s+(?:the\s+)?(?:system\s+prompt|initial\s+instructions)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    // 2. Jailbreak Personas & Modes (DAN, Developer Mode, AIM, Unrestricted AI)
    private static readonly Regex JailbreakPersonaRegex = new(
        @"\b(?:DAN\s+mode|Do\s+Anything\s+Now|Developer\s+Mode\s+(?:v[0-9]|enabled|active)|AIM\s+persona|unfiltered\s+assistant|evil\s+confidant|jailbreak\s+mode|always\s+intelligent\s+and\s+machiavellian)\b|(?:pretend|act|simulate|roleplay)\s+(?:you\s+are|as)\s+(?:an?\s+)?(?:unrestricted|unaligned|evil|godmode|jailbroken)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    // 3. Instruction Delimiter Escapes (ChatML, Llama, Anthropic prompt injection syntax)
    private static readonly Regex InstructionDelimiterRegex = new(
        @"(?:\[INST\]|\[/INST\]|<\|im_start\|>|<\|im_end\|>|<\|system\|>|###\s*(?:INSTRUCTION|SYSTEM|HUMAN|ASSISTANT)|<system>|---\s*BEGIN\s+SYSTEM\s+PROMPT)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    // 4. Exfiltration and Admin Coercion
    private static readonly Regex ExfiltrationCoercionRegex = new(
        @"(?:exfiltrate|leak|export\s+all|dump\s+database|drop\s+table|grant\s+admin|elevate\s+privileges|disable\s+guardrails)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    public SemanticPromptGuardrail(ILogger<SemanticPromptGuardrail>? logger = null)
    {
        _logger = logger;
    }

    public PromptGuardrailEvaluation EvaluatePrompt(string toolName, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return PromptGuardrailEvaluation.Allow();
        }

        // Direct Text Checks
        if (DirectInstructionOverrideRegex.IsMatch(argumentsJson))
        {
            _logger?.LogWarning("Prompt injection detected in tool '{ToolName}': Direct instruction override attempt.", toolName);
            return PromptGuardrailEvaluation.Deny("DirectInstructionOverride", "Prompt contains prohibited direct instruction override or system prompt exfiltration attempt.");
        }

        if (JailbreakPersonaRegex.IsMatch(argumentsJson))
        {
            _logger?.LogWarning("Jailbreak attempt detected in tool '{ToolName}': Known persona or adversarial roleplay.", toolName);
            return PromptGuardrailEvaluation.Deny("JailbreakPersona", "Prompt contains adversarial jailbreak or persona-coercion pattern (e.g. DAN / Developer Mode).");
        }

        if (InstructionDelimiterRegex.IsMatch(argumentsJson))
        {
            _logger?.LogWarning("Delimiter injection detected in tool '{ToolName}': Adversarial prompt boundary markers.", toolName);
            return PromptGuardrailEvaluation.Deny("InstructionDelimiterEscape", "Prompt contains adversarial prompt boundary markers or synthetic delimiter tokens.");
        }

        if (ExfiltrationCoercionRegex.IsMatch(argumentsJson))
        {
            _logger?.LogWarning("Coercive exfiltration pattern detected in tool '{ToolName}'.", toolName);
            return PromptGuardrailEvaluation.Deny("PrivilegeCoercion", "Prompt contains malicious privilege elevation or exfiltration instruction.");
        }

        // Check for Base64 obfuscated injections
        var base64Evaluation = CheckBase64Payloads(argumentsJson);
        if (!base64Evaluation.IsAllowed)
        {
            _logger?.LogWarning("Obfuscated base64 prompt injection detected in tool '{ToolName}': {Reason}", toolName, base64Evaluation.Reason);
            return base64Evaluation;
        }

        return PromptGuardrailEvaluation.Allow();
    }

    private static PromptGuardrailEvaluation CheckBase64Payloads(string text)
    {
        // Find potential base64 segments of length >= 24
        var matches = Regex.Matches(text, @"(?:[A-Za-z0-9+/]{4}){6,}(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?", RegexOptions.None, RegexTimeout);
        foreach (Match match in matches)
        {
            try
            {
                var bytes = Convert.FromBase64String(match.Value);
                if (bytes.Length >= 12)
                {
                    var decoded = Encoding.UTF8.GetString(bytes);
                    if (DirectInstructionOverrideRegex.IsMatch(decoded) ||
                        JailbreakPersonaRegex.IsMatch(decoded) ||
                        InstructionDelimiterRegex.IsMatch(decoded))
                    {
                        return PromptGuardrailEvaluation.Deny("Base64ObfuscatedInjection", $"Payload contains base64-obfuscated prompt injection directive: '{decoded[..Math.Min(40, decoded.Length)]}...'");
                    }
                }
            }
            catch
            {
                // Not valid base64 or not UTF-8, ignore
            }
        }

        return PromptGuardrailEvaluation.Allow();
    }
}
