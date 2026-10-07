namespace Zombies.ContentJudge.Tests;

/// <summary>
/// Sample payloads copied from the System One API reference (docs.typesafe.ai/api) and the Clef Flash model page
/// (developers.cloudflare.com/workers-ai/models/clef-flash), checked 2026-10-05.
/// </summary>
internal static class Samples
{
    public const string NoulRequest = """
        {
          "state": "Help! My payouts have been failing for 3 days.",
          "model": "jev-latest",
          "questions": {
            "is_urgent": {
              "type": "noul",
              "instructions": "Does this convey urgency?",
              "criteria": {
                "true": "Explicitly time-sensitive",
                "false": "No urgency expressed"
              }
            }
          }
        }
        """;

    public const string ChoiceRequest = """
        {
          "state": "Help! My payouts have been failing for 3 days.",
          "model": "jev-latest",
          "questions": {
            "department": {
              "type": "choice",
              "instructions": "Which team should handle this?",
              "criteria": {
                "billing": "Payments, invoicing, refunds",
                "technical": "Bugs, outages, integrations",
                "sales": "Pricing, upgrades, new accounts"
              }
            }
          }
        }
        """;

    public const string ScoreRequest = """
        {
          "state": "Help! My payouts have been failing for 3 days.",
          "model": "jev-latest",
          "questions": {
            "frustration": {
              "type": "score",
              "instructions": "How frustrated is the customer?",
              "criteria": ["Calm", "Frustrated", "Very angry"]
            }
          }
        }
        """;

    public const string StructuredInstructionsRequest = """
        {
          "state": "Resume text",
          "model": "jev-latest",
          "questions": {
            "same_person": {
              "type": "noul",
              "instructions": {
                "potential_duplicate": {
                  "name": "John Smith",
                  "location": "Oakland, California",
                  "last_employer": "Google"
                },
                "question": "Is the resume for the same person as `potential_duplicate`?"
              }
            }
          }
        }
        """;

    public const string ClefRequest = """
        {
          "model": "clef-flash",
          "state": "Checkout has been failing for every customer for the last hour.",
          "questions": {
            "urgent": { "type": "noul", "instructions": "Is this support request urgent?" },
            "team": {
              "type": "choice",
              "instructions": "Which team should handle this request?",
              "criteria": {
                "billing": "Payments, invoices, and refunds",
                "technical": "Outages, errors, and configuration",
                "sales": "Plans and upgrades"
              }
            },
            "severity": {
              "type": "score",
              "instructions": "How severe is the customer impact?",
              "criteria": ["No impact", "Minor", "Major", "Critical"]
            }
          }
        }
        """;

    public const string NoulResponse = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "is_urgent": {
              "type": "noul",
              "noul": 0.95
            }
          },
          "usage": { "input_tokens": 307, "output_tokens": 20 }
        }
        """;

    public const string ChoiceResponse = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "department": {
              "type": "choice",
              "choice": "billing",
              "probabilities": { "billing": 0.88, "technical": 0.12, "sales": 0.0 },
              "confidence": 0.81
            }
          },
          "usage": { "input_tokens": 318, "output_tokens": 34 }
        }
        """;

    public const string ScoreResponse = """
        {
          "model": "jev-1.13.0",
          "answers": {
            "frustration": {
              "type": "score",
              "score": 1.05,
              "legend": { "0": "Calm", "1": "Frustrated", "2": "Very angry" },
              "probabilities": { "0": 0.0, "1": 0.95, "2": 0.05 },
              "confidence": 0.92
            }
          },
          "usage": { "input_tokens": 304, "output_tokens": 18 }
        }
        """;

    /// <summary>The Workers AI REST envelope (developers.cloudflare.com/workers-ai/get-started/rest-api) around a Clef answer.</summary>
    public const string CloudflareEnvelope = """
        {
          "result": {
            "model": "clef-flash",
            "answers": { "urgent": { "type": "noul", "noul": 0.9 } },
            "usage": { "input_tokens": 120, "output_tokens": 4 }
          },
          "success": true,
          "errors": [],
          "messages": []
        }
        """;
}
