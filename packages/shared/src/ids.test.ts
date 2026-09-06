import { describe, expect, it } from 'vitest'
import { newMessageId } from './ids.js'

describe('newMessageId', () => {
  it('returns a v7 uuid', () => {
    const id = newMessageId()
    expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
  })

  it('sorts chronologically as a string', async () => {
    const first = newMessageId()
    await new Promise((r) => setTimeout(r, 5))
    const second = newMessageId()
    expect(first < second).toBe(true)
  })
})
