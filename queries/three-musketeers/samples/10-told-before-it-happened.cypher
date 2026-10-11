// What happened earlier than it was told?
// Referenced events: only mentioned, with a story time earlier than the event that mentions them.
// Parameters: $pos (the timeline's chapter end), $spoilers. visible(x) is the spoiler rule on a node or an edge.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids), hidden in the results grid.
MATCH (e:Event)-[m:MENTIONED_IN]->(n:Event) WHERE e.kind = 'referenced' AND visible(e) AND visible(m) AND visible(n)
RETURN e.summary AS event, e.storyFrom AS storyTime, n.summary AS mentionedIn, e.revealedPos AS revealed, id(e) AS eventId, id(n) AS mentionedInId, id(m) AS edgeId
ORDER BY e.storyFromLo, e.revealedPos
