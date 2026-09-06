import { AsyncLocalStorage } from 'node:async_hooks'

const storage = new AsyncLocalStorage<{ tenantId: string }>()

/**
 * Ngữ cảnh tenant của request hoặc kết nối hiện tại.
 *
 * Mọi truy cập DB nghiệp vụ phải nằm trong một `run`. `currentTenantId` ném lỗi
 * thay vì trả null khi thiếu ngữ cảnh: nếu trả null thì một chỗ quên gọi `run`
 * sẽ lặng lẽ đọc nhầm database của bệnh viện khác.
 */
export const TenantContext = {
  run<T>(tenantId: string, fn: () => Promise<T>): Promise<T> {
    return storage.run({ tenantId }, fn)
  },

  currentTenantId(): string {
    const store = storage.getStore()
    if (!store) {
      throw new Error('không có ngữ cảnh tenant: truy cập DB nghiệp vụ ngoài TenantContext.run')
    }
    return store.tenantId
  },
}
