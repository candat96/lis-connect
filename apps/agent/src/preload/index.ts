import { contextBridge, ipcRenderer } from 'electron'

contextBridge.exposeInMainWorld('agent', {
  invoke: (channel: string, payload?: unknown) => ipcRenderer.invoke(channel, payload),
  on: (channel: string, handler: (payload: unknown) => void) => {
    const listener = (_event: unknown, payload: unknown): void => handler(payload)
    ipcRenderer.on(channel, listener)
    return () => ipcRenderer.off(channel, listener)
  },
})
