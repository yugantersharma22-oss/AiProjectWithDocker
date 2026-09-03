using System;
using System.Collections.Generic;

namespace GenAiProject.Models;

public partial class KnowledgeBase
{
    public int Id { get; set; }

    public string Content { get; set; } = null!;

    public DateTime? CreatedAt { get; set; }
}
