// Menu text audit, run in play mode through the editor's run_csharp tool (sbox MCP / sbox-cli):
// replace "PAGE" with a MenuPageIds value (inventory, crafting, quests, skills, map, settings, augments,
// augment_station) or with "keep" to audit whatever is open. It reports every visible label that an
// ancestor's overflow cuts (CLIP), every uppercase heading that wrapped (WRAP), every label box shorter
// than its font (SHORT) and every pair of labels drawn over each other (OVERLAP), in 1080p design pixels.
// It measures the live viewport, so run it at the aspect you care about; select the longest recipe /
// quest / augment first - the detail blocks are fixed height and overflow only with real content.
var scene = global::Sandbox.Game.ActiveScene;
var hud = scene.GetAllComponents<Survival.PlayerScreenHud>().FirstOrDefault( h => h.Panel is not null && h.Panel.IsValid() );
if ( hud is null ) return "no hud";
var ctrl = scene.GetAllComponents<Survival.PlayerGameMenuController>().FirstOrDefault();
if ( ctrl is null ) return "no menu controller";
var page = "PAGE";
if ( page != "keep" )
{
	ctrl.SetActivePage( page );
	await global::Sandbox.GameTask.DelayRealtime( 500 );
}

var sb = new System.Text.StringBuilder();
var root = hud.Panel;
var scale = root.ScaleToScreen <= 0f ? 1f : root.ScaleToScreen;
sb.Append( "page=" + page + " open=" + ctrl.IsMenuOpen + " panels=" + ctrl.VisiblePanels + " scale=" + scale.ToString( "0.###" ) + " root=" + root.Box.Rect.Width.ToString( "0" ) + "x" + root.Box.Rect.Height.ToString( "0" ) + "|" );

var labels = new System.Collections.Generic.List<(global::Sandbox.UI.Label L, global::Sandbox.Rect R, global::Sandbox.Rect Clip, float Font, bool Heading)>();
var stack = new System.Collections.Generic.Stack<(global::Sandbox.UI.Panel P, global::Sandbox.Rect Clip)>();
stack.Push( (root, root.Box.Rect) );
while ( stack.Count > 0 )
{
	var (p, clip) = stack.Pop();
	if ( p is null || !p.IsValid() || !p.IsVisible ) continue;
	var cs = p.ComputedStyle;
	if ( cs is null ) continue;
	if ( cs.Opacity.HasValue && cs.Opacity.Value < 0.05f ) continue;
	var r = p.Box.Rect;
	if ( p is global::Sandbox.UI.Label l && !string.IsNullOrWhiteSpace( l.Text ) )
	{
		var font = cs.FontSize.HasValue ? cs.FontSize.Value.GetPixels( 1f ) : 0f;
		var heading = cs.TextTransform.HasValue && cs.TextTransform.Value.ToString().ToLowerInvariant().Contains( "upper" );
		labels.Add( (l, r, clip, font, heading) );
	}
	var childClip = clip;
	var ox = cs.OverflowX.HasValue ? cs.OverflowX.Value.ToString() : "Visible";
	var oy = cs.OverflowY.HasValue ? cs.OverflowY.Value.ToString() : "Visible";
	if ( ox != "Visible" || oy != "Visible" )
	{
		var left = ox != "Visible" ? System.MathF.Max( clip.Left, r.Left ) : clip.Left;
		var right = ox != "Visible" ? System.MathF.Min( clip.Right, r.Right ) : clip.Right;
		var top = oy != "Visible" ? System.MathF.Max( clip.Top, r.Top ) : clip.Top;
		var bottom = oy != "Visible" ? System.MathF.Min( clip.Bottom, r.Bottom ) : clip.Bottom;
		childClip = new global::Sandbox.Rect( left, top, System.MathF.Max( 0f, right - left ), System.MathF.Max( 0f, bottom - top ) );
	}
	foreach ( var c in p.Children ) stack.Push( (c, childClip) );
}

sb.Append( "labels=" + labels.Count + "|" );
string D( global::Sandbox.Rect r ) => ( r.Left / scale ).ToString( "0" ) + "," + ( r.Top / scale ).ToString( "0" ) + " " + ( r.Width / scale ).ToString( "0" ) + "x" + ( r.Height / scale ).ToString( "0" );
string T( string s ) { s = s.Replace( "|", "/" ).Replace( "\n", " " ); return s.Length > 28 ? s.Substring( 0, 28 ) : s; }
var tol = 1.5f * scale;
for ( var i = 0; i < labels.Count; i++ )
{
	var a = labels[i];
	if ( a.R.Width < 1f || a.R.Height < 1f ) continue;
	var cutL = a.Clip.Left - a.R.Left; var cutR = a.R.Right - a.Clip.Right; var cutT = a.Clip.Top - a.R.Top; var cutB = a.R.Bottom - a.Clip.Bottom;
	var fullyHidden = a.R.Right <= a.Clip.Left || a.R.Left >= a.Clip.Right || a.R.Bottom <= a.Clip.Top || a.R.Top >= a.Clip.Bottom;
	if ( !fullyHidden && ( cutL > tol || cutR > tol || cutT > tol || cutB > tol ) )
		sb.Append( "CLIP [" + T( a.L.Text ) + "] " + D( a.R ) + " cut L" + ( cutL / scale ).ToString( "0" ) + " R" + ( cutR / scale ).ToString( "0" ) + " T" + ( cutT / scale ).ToString( "0" ) + " B" + ( cutB / scale ).ToString( "0" ) + "|" );
	if ( fullyHidden ) continue;
	if ( a.Heading && a.Font > 0f && a.R.Height > a.Font * 2.1f )
		sb.Append( "WRAP [" + T( a.L.Text ) + "] " + D( a.R ) + " font " + ( a.Font / scale ).ToString( "0.#" ) + "|" );
	if ( a.Font > 0f && a.R.Height < a.Font * 1.05f )
		sb.Append( "SHORT [" + T( a.L.Text ) + "] " + D( a.R ) + " font " + ( a.Font / scale ).ToString( "0.#" ) + "|" );
	for ( var j = i + 1; j < labels.Count; j++ )
	{
		var b = labels[j];
		if ( b.R.Width < 1f || b.R.Height < 1f ) continue;
		if ( b.R.Right <= b.Clip.Left || b.R.Left >= b.Clip.Right || b.R.Bottom <= b.Clip.Top || b.R.Top >= b.Clip.Bottom ) continue;
		var ix = System.MathF.Min( a.R.Right, b.R.Right ) - System.MathF.Max( a.R.Left, b.R.Left );
		var iy = System.MathF.Min( a.R.Bottom, b.R.Bottom ) - System.MathF.Max( a.R.Top, b.R.Top );
		if ( ix <= 2f * scale || iy <= 4f * scale ) continue;
		// A label inside another label's panel (button caption on its button) is not an overlap.
		var nested = false;
		for ( var q = a.L.Parent; q is not null; q = q.Parent ) if ( q == b.L ) nested = true;
		for ( var q = b.L.Parent; q is not null; q = q.Parent ) if ( q == a.L ) nested = true;
		if ( nested ) continue;
		sb.Append( "OVERLAP [" + T( a.L.Text ) + "] " + D( a.R ) + " x [" + T( b.L.Text ) + "] " + D( b.R ) + "|" );
	}
}
return sb.ToString();
