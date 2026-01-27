using RandomCodeAnalysis.Models.MethodChain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RandomCodeAnalysis
{
    class JiraHelper
    {
        public static void WriteJson(string jsonString)
        {
            //output the jsonstring somewhere in a file and print the file name/location
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"JiraTicket_{timestamp}.json";
            var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "Output", fileName);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, jsonString);

            Console.WriteLine($"JSON output written to: {outputPath}");
        }

        const string descriptionBlob =
            """
                        {
              "version": 1,
              "type": "doc",
              "content": [
                {
                  "type": "panel",
                  "attrs": {
                    "panelType": "info"
                  },
                  "content": [
                    {
                      "type": "paragraph",
                      "content": [
                        {
                          "type": "text",
                          "text": "Short Description",
                          "marks": [
                            {
                              "type": "strong"
                            }
                          ]
                        }
                      ]
                    },
                    {
                      "type": "paragraph",
                      "content": [
                        {
                          "type": "text",
                          "text": "Convert "
                        },
                        {
                          "type": "text",
                          "text": "METHODNAME",
                          "marks": [
                            {
                              "type": "code"
                            }
                          ]
                        },
                        {
                          "type": "text",
                          "text": " to reference "
                        },
                        {
                          "type": "text",
                          "text": "MakeCallAsync",
                          "marks": [
                            {
                              "type": "code"
                            }
                          ]
                        },
                        {
                          "type": "text",
                          "text": " and update it’s full call chain to "
                        },
                        {
                          "type": "text",
                          "text": "Task/Task<T>",
                          "marks": [
                            {
                              "type": "code"
                            }
                          ]
                        },
                        {
                          "type": "text",
                          "text": " signatures with "
                        },
                        {
                          "type": "text",
                          "text": "await",
                          "marks": [
                            {
                              "type": "code"
                            }
                          ]
                        },
                        {
                          "type": "text",
                          "text": " propagation"
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "panel",
                  "attrs": {
                    "panelType": "info"
                  },
                  "content": [
                    {
                      "type": "paragraph",
                      "content": [
                        {
                          "type": "text",
                          "text": "Business Case",
                          "marks": [
                            {
                              "type": "strong"
                            }
                          ]
                        }
                      ]
                    },
                    {
                      "type": "paragraph",
                      "content": [
                        {
                          "type": "text",
                          "text": "See "
                        },
                        {
                          "type": "text",
                          "text": "Epic",
                          "marks": [
                            {
                              "type": "link",
                              "attrs": {
                                "href": "https://my.work.tylertech.com/browse/OS-14002"
                              }
                            }
                          ]
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "panel",
                  "attrs": {
                    "panelType": "info"
                  },
                  "content": [
                    {
                      "type": "paragraph",
                      "content": [
                        {
                          "type": "text",
                          "text": "Notes",
                          "marks": [
                            {
                              "type": "strong"
                            }
                          ]
                        }
                      ]
                    },
                    {
                      "type": "bulletList",
                      "content": [
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "Call and await 'MakeCallAsync'",
                                  "marks": [
                                    {
                                      "type": "strong"
                                    }
                                  ]
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "Update METHODNAME method signature to return an async Task<>"
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "All other async methods must contain an ",
                                  "marks": [
                                    {
                                      "type": "strong"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": "await",
                                  "marks": [
                                    {
                                      "type": "code"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": "."
                                }
                              ]
                            },
                            {
                              "type": "bulletList",
                              "content": [
                                {
                                  "type": "listItem",
                                  "content": [
                                    {
                                      "type": "paragraph",
                                      "content": [
                                        {
                                          "type": "text",
                                          "text": "If a method has no work other than calling another async method, it should "
                                        },
                                        {
                                          "type": "text",
                                          "text": "await",
                                          "marks": [
                                            {
                                              "type": "code"
                                            }
                                          ]
                                        },
                                        {
                                          "type": "text",
                                          "text": " that call."
                                        }
                                      ]
                                    }
                                  ]
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "Preserve behavior:"
                                }
                              ]
                            },
                            {
                              "type": "bulletList",
                              "content": [
                                {
                                  "type": "listItem",
                                  "content": [
                                    {
                                      "type": "paragraph",
                                      "content": [
                                        {
                                          "type": "text",
                                          "text": "Do "
                                        },
                                        {
                                          "type": "text",
                                          "text": "not",
                                          "marks": [
                                            {
                                              "type": "strong"
                                            }
                                          ]
                                        },
                                        {
                                          "type": "text",
                                          "text": " introduce concurrency."
                                        }
                                      ]
                                    }
                                  ]
                                },
                                {
                                  "type": "listItem",
                                  "content": [
                                    {
                                      "type": "paragraph",
                                      "content": [
                                        {
                                          "type": "text",
                                          "text": "Do "
                                        },
                                        {
                                          "type": "text",
                                          "text": "not",
                                          "marks": [
                                            {
                                              "type": "strong"
                                            }
                                          ]
                                        },
                                        {
                                          "type": "text",
                                          "text": " add fire-and-forget calls."
                                        }
                                      ]
                                    },
                                    {
                                      "type": "bulletList",
                                      "content": [
                                        {
                                          "type": "listItem",
                                          "content": [
                                            {
                                              "type": "paragraph",
                                              "content": [
                                                {
                                                  "type": "text",
                                                  "text": "Careful when updating void methods"
                                                }
                                              ]
                                            }
                                          ]
                                        }
                                      ]
                                    }
                                  ]
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "Use compiler errors to drive fixes; do not refactor beyond what is required to compile."
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "Warnings about “async method lacks await” are acceptable "
                                },
                                {
                                  "type": "text",
                                  "text": "only",
                                  "marks": [
                                    {
                                      "type": "strong"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": " for the designated core method."
                                }
                              ]
                            }
                          ]
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "panel",
                  "attrs": {
                    "panelType": "info"
                  },
                  "content": [
                    {
                      "type": "paragraph",
                      "content": [
                        {
                          "type": "text",
                          "text": "Acceptance Criteria",
                          "marks": [
                            {
                              "type": "strong"
                            }
                          ]
                        }
                      ]
                    },
                    {
                      "type": "bulletList",
                      "content": [
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "METHODNAME",
                                  "marks": [
                                    {
                                      "type": "code"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": " is converted to "
                                },
                                {
                                  "type": "text",
                                  "text": "Task",
                                  "marks": [
                                    {
                                      "type": "code"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": " / ",
                                  "marks": [
                                    {
                                      "type": "strong"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": "Task<T>",
                                  "marks": [
                                    {
                                      "type": "code"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": " return type."
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "All upstream callers are updated to compile using "
                                },
                                {
                                  "type": "text",
                                  "text": "async",
                                  "marks": [
                                    {
                                      "type": "code"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": " / ",
                                  "marks": [
                                    {
                                      "type": "strong"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": "await",
                                  "marks": [
                                    {
                                      "type": "code"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": " or valid task passthrough."
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "Rename methods to ABCAsync when they are made async"
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "MakeCall is replaced with ",
                                  "marks": [
                                    {
                                      "type": "strong"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": "await MakeCallAsync",
                                  "marks": [
                                    {
                                      "type": "code"
                                    }
                                  ]
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "No fire-and-forget behavior",
                                  "marks": [
                                    {
                                      "type": "strong"
                                    }
                                  ]
                                },
                                {
                                  "type": "text",
                                  "text": " is introduced:"
                                }
                              ]
                            },
                            {
                              "type": "bulletList",
                              "content": [
                                {
                                  "type": "listItem",
                                  "content": [
                                    {
                                      "type": "paragraph",
                                      "content": [
                                        {
                                          "type": "text",
                                          "text": "Every "
                                        },
                                        {
                                          "type": "text",
                                          "text": "Task",
                                          "marks": [
                                            {
                                              "type": "code"
                                            }
                                          ]
                                        },
                                        {
                                          "type": "text",
                                          "text": " returned is awaited or returned."
                                        }
                                      ]
                                    }
                                  ]
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "Runtime behavior is preserved:"
                                }
                              ]
                            },
                            {
                              "type": "bulletList",
                              "content": [
                                {
                                  "type": "listItem",
                                  "content": [
                                    {
                                      "type": "paragraph",
                                      "content": [
                                        {
                                          "type": "text",
                                          "text": "Request ordering, exception behavior, and timing remain equivalent."
                                        }
                                      ]
                                    }
                                  ]
                                }
                              ]
                            }
                          ]
                        },
                        {
                          "type": "listItem",
                          "content": [
                            {
                              "type": "paragraph",
                              "content": [
                                {
                                  "type": "text",
                                  "text": "Change is validated via:"
                                }
                              ]
                            },
                            {
                              "type": "bulletList",
                              "content": [
                                {
                                  "type": "listItem",
                                  "content": [
                                    {
                                      "type": "paragraph",
                                      "content": [
                                        {
                                          "type": "text",
                                          "text": "successful compilation"
                                        }
                                      ]
                                    }
                                  ]
                                },
                                {
                                  "type": "listItem",
                                  "content": [
                                    {
                                      "type": "paragraph",
                                      "content": [
                                        {
                                          "type": "text",
                                          "text": "basic smoke tests confirming no functional change."
                                        }
                                      ]
                                    }
                                  ]
                                }
                              ]
                            }
                          ]
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "paragraph",
                  "content": []
                }
              ]
            }
            """;

        const string jiraTicketBlob = """
                     {
                      "fields": {
                        "project": { "key": "OS" },
                        "summary": "STORYNAME",
                        "description": DESCRIPTION,
                        "issuetype": { "name": "Story" }
                      }
                    }
                    """;

        public static string GenerateJiraTicketString(MethodReferenceNode node)
        {
            var jsonStringBuilder = new StringBuilder();
            jsonStringBuilder.AppendLine("[");

            foreach (var callerNode in node.CallerNodes)
            {
                var ticket = jiraTicketBlob.Replace("STORYNAME", $"{callerNode.SimpleMethodName} Async Conversion");
                ticket = ticket.Replace("DESCRIPTION", descriptionBlob.Replace("METHODNAME", callerNode.MethodName));
                jsonStringBuilder.AppendLine(ticket);
                jsonStringBuilder.AppendLine(",");
            }

            jsonStringBuilder.AppendLine("]");

            return jsonStringBuilder.ToString();
        }

    }
}
