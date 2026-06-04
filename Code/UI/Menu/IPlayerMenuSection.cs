using Sandbox.UI;

namespace Survival;

/// <summary>One panel section inside <see cref="PlayerScreenHud"/> (inventory, crafting, etc.).</summary>
public interface IPlayerMenuSection
{
	string SectionId { get; }

	/// <summary>Build once under the shared menu column.</summary>
	void Build( Panel menuColumn );

	/// <summary>Refresh slot labels/icons when underlying data changes.</summary>
	void Refresh();

	/// <summary>Section visibility follows the menu open state.</summary>
	void SetMenuOpen( bool isOpen );
}
