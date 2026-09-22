using LoveCapsule.Api.Data;

namespace LoveCapsule.Api.Models;

public record CreateMemoryRequest(
	string Title,
	DateTime Date,
	string Mood,
	string Description,
	string? ImageUrl = null,
	MemoryVisibility Visibility = MemoryVisibility.Private,
	bool PartnerCanEdit = false);
