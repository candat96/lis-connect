interface AgentBridge {
  invoke: <T>(channel: string, payload?: unknown) => Promise<T>
  on: (channel: string, handler: (payload: unknown) => void) => () => void
}

interface Window {
  agent: AgentBridge
}
