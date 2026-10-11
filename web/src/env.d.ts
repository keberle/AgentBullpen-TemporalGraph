/// <reference types="vite/client" />

// build-time settings. Both empty (the default) = standalone; set = behind the AgentBullpen.AI gateway (see graphql.ts).
interface ImportMetaEnv {
  /** The site's API base, e.g. https://api.agentbullpen.ai; GraphQL then goes to <base>/temporal/graphql. */
  readonly VITE_API_BASE_URL?: string;
  /** The site's address: its sign-in page (signed out, or a 401 later), the user menu's links, and the header's link to the site. */
  readonly VITE_SITE_URL?: string;
}
