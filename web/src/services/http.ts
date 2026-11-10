import { useSessionStore } from '@/stores/sessionStore';

/** Lỗi từ API kèm mã HTTP; `message` là câu tiếng Việt server trả về, hiển thị thẳng cho người dùng. */
export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

type Query = Record<string, string | number | undefined | null>;

/**
 * Gọi API của server (cùng origin, tiền tố /api). Tự gắn token phiên; gặp 401 khi đang có phiên
 * thì xoá phiên — màn hình sẽ tự chuyển về đăng nhập.
 */
export async function api<T>(path: string, options: { method?: string; body?: unknown; query?: Query } = {}): Promise<T> {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(options.query ?? {})) {
    if (value !== undefined && value !== null && value !== '') params.set(key, String(value));
  }
  const url = `/api${path}${params.size > 0 ? `?${params}` : ''}`;

  const token = useSessionStore.getState().token;
  let response: Response;
  try {
    response = await fetch(url, {
      method: options.method ?? 'GET',
      headers: {
        ...(options.body !== undefined ? { 'content-type': 'application/json' } : {}),
        ...(token ? { authorization: `Bearer ${token}` } : {}),
      },
      body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
    });
  } catch {
    throw new ApiError('Không kết nối được máy chủ — kiểm tra mạng rồi thử lại', 0);
  }

  if (!response.ok) {
    if (response.status === 401 && token) useSessionStore.getState().clear();
    throw new ApiError(await readMessage(response), response.status);
  }
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

async function readMessage(response: Response): Promise<string> {
  try {
    const body = (await response.json()) as { message?: string | string[] };
    const message = Array.isArray(body.message) ? body.message.join('; ') : body.message;
    if (message) return message;
  } catch {
    /* không phải JSON */
  }
  return response.status >= 500 ? 'Máy chủ gặp lỗi — thử lại sau' : `Yêu cầu bị từ chối (${response.status})`;
}
