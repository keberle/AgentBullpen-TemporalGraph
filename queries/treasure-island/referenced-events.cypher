// referenced_events(pos): events that are only mentioned (their story time is earlier than where the reader hears of them), with the event that mentions each.
// Parameters: $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (the graph highlight).
MATCH (e:Event)-[m:MENTIONED_IN]->(n:Event) WHERE e.kind = 'referenced' AND visible(e) AND visible(m) AND visible(n)
RETURN e.summary AS event, e.storyFrom AS storyTime, n.summary AS mentionedIn, e.revealedPos AS revealed, id(e) AS eventId, id(n) AS mentionedInId, id(m) AS edgeId
ORDER BY e.storyFromLo, e.revealedPos
