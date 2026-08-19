using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text;

namespace MeetingRecord.Models.Others;

public class RecordVm
{
    [Required, StringLength(50)]
    public string Name { get; set; } = "";

    [Range(0, 150)]
    public int Age { get; set; } = 30;

    [Required]
    public DateTime? VisitDate { get; set; } = DateTime.Today;

    [Required]
    public string Department { get; set; } = "OPD";

    [Range(0, 10)]
    public double PainScore { get; set; } = 0;     // 拉霸常用 0~10

    [StringLength(500)]
    public string Notes { get; set; } = "";

    public bool IsActive { get; set; } = true;

    [Required]
    public string Gender { get; set; } = "M";   // Radio 常用 string/enum
}