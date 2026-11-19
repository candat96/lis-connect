// Hộp thoại ghép nối: mở ngay sau khi tạo agent hoặc bấm "Ghép nối lại".
// Bước 1 tải tệp cài (tên tệp mang mã), bước 2 chạy tệp trên máy tính cạnh máy xét nghiệm; hộp thoại tự theo dõi tới khi agent kết nối.
import { CheckCircleFilled, ClockCircleOutlined, CopyOutlined, DownloadOutlined, LoadingOutlined, ReloadOutlined } from '@ant-design/icons';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Alert, App, Button, Flex, Input, Modal, Space, Steps, Typography } from 'antd';
import dayjs from 'dayjs';
import { useEffect, useState } from 'react';
import { agentService, qk, type PairingTicket } from '@/services';
import { installerFileName } from './pairing';

export interface PairingTarget {
  agentId: string;
  agentName: string;
  ticket: PairingTicket;
  /** `pairedAt` của agent lúc mở hộp thoại: mã mới đã được dùng khi `pairedAt` đổi khác giá trị này. */
  baselinePairedAt: string | null;
}

interface Props {
  target: PairingTarget | null;
  onClose: () => void;
  /** Tạo mã mới khi mã đang hiện đã hết hạn. */
  onNewCode: () => void;
  newCodeLoading: boolean;
}

export function PairingDialog({ target, onClose, onNewCode, newCodeLoading }: Props) {
  const open = target !== null;
  const { message } = App.useApp();
  const now = useNow(open);

  const installer = useQuery({ queryKey: qk.agentInstaller, queryFn: agentService.installerInfo, enabled: open });
  // Cùng khoá với bảng agent: hỏi mỗi 2 giây khi hộp thoại mở, bảng phía sau cũng cập nhật theo.
  const agents = useQuery({ queryKey: qk.agents, queryFn: agentService.list, enabled: open, refetchInterval: open ? 2_000 : false });

  const agent = target ? agents.data?.find((a) => a.id === target.agentId) : undefined;
  const used = !!target && !!agent?.pairedAt && agent.pairedAt !== target.baselinePairedAt;
  const expired = !!target && !used && now >= new Date(target.ticket.expiresAt).getTime();

  const download = useMutation({
    mutationFn: async (code: string) => {
      const { blob, headers } = await agentService.downloadInstaller();
      await checkSha256(blob, headers.get('X-Agent-Sha256'));
      const url = URL.createObjectURL(blob);
      try {
        const link = document.createElement('a');
        link.href = url;
        link.download = installerFileName(code);
        document.body.appendChild(link);
        link.click();
        link.remove();
      } finally {
        // Trình duyệt cần URL còn sống tới khi bắt đầu lưu tệp.
        setTimeout(() => URL.revokeObjectURL(url), 60_000);
      }
    },
    onError: (e) => message.error((e as Error).message),
  });

  const copyCode = async () => {
    if (!target) return;
    try {
      await navigator.clipboard.writeText(target.ticket.code);
      message.success('Đã chép mã');
    } catch {
      message.warning('Trình duyệt không cho chép tự động — bôi đen mã rồi Ctrl+C');
    }
  };

  const available = installer.data?.available ?? false;

  return (
    <Modal open={open} title={target ? `Ghép nối "${target.agentName}"` : ''} width={720} destroyOnHidden
      maskClosable={false} onCancel={onClose}
      footer={<Button type={used ? 'primary' : 'default'} onClick={onClose}>{used ? 'Xong' : 'Đóng'}</Button>}>
      {target && (
        <Flex vertical gap={16}>
          <LiveStatus used={used} expired={expired} online={agent?.online ?? false} machineName={agent?.machineName ?? null}
            onNewCode={onNewCode} newCodeLoading={newCodeLoading} />

          <Steps direction="vertical" size="small" current={used ? 2 : -1} items={[
            {
              title: 'Tải tệp cài',
              description: (
                <Flex vertical gap={8} style={{ paddingBlock: 4 }}>
                  {installer.data && !available && (
                    <Alert type="warning" showIcon message="Máy chủ chưa có tệp cài agent"
                      description={<>Người quản trị máy chủ cần đặt <span className="code">LisAgent.exe</span> vào thư mục khai báo ở biến <span className="code">AGENT_DIST_DIR</span> (xem hướng dẫn cài đặt). Trong lúc chờ, vẫn có thể dùng mã bên dưới với tệp cài sẵn có.</>} />
                  )}
                  {installer.error && <Alert type="error" showIcon message={(installer.error as Error).message} />}
                  <Space wrap>
                    <Button type="primary" icon={<DownloadOutlined />} disabled={!available || expired || used} loading={download.isPending}
                      onClick={() => download.mutate(target.ticket.code)}>Tải tệp cài</Button>
                    {installer.data?.version && <Typography.Text type="secondary">Phiên bản {installer.data.version}</Typography.Text>}
                  </Space>
                  <Typography.Text type="secondary">Tên tệp đã chứa mã ghép nối — giữ nguyên tên khi chép sang máy khác.</Typography.Text>
                </Flex>
              ),
            },
            {
              title: 'Chạy tệp trên máy tính cạnh máy xét nghiệm',
              description: (
                <Flex vertical gap={6} style={{ paddingBlock: 4 }}>
                  <Typography.Text>Nhấp đúp tệp vừa tải. Nếu Windows hiện <b>"Windows protected your PC"</b>, bấm <b>More info</b> → <b>Run anyway</b>. Ở hộp thoại quyền quản trị, bấm <b>Có / Yes</b>.</Typography.Text>
                  <Typography.Text>Tệp tự cài thành dịch vụ "LIS Agent", chạy cùng Windows — không cần sửa tệp cấu hình nào.</Typography.Text>
                  <Typography.Text type="secondary">Nếu tên tệp bị đổi (chép qua USB, tải lại trùng tên…), tệp cài sẽ hỏi mã — dán mã này vào:</Typography.Text>
                  <Space.Compact style={{ width: '100%' }}>
                    <Input readOnly value={target.ticket.code} className="code" aria-label="Mã ghép nối" onFocus={(e) => e.target.select()} />
                    <Button icon={<CopyOutlined />} onClick={copyCode}>Chép mã</Button>
                  </Space.Compact>
                  <Typography.Text type="secondary">
                    <ClockCircleOutlined /> Mã dùng một lần, hết hạn lúc <b>{dayjs(target.ticket.expiresAt).format('HH:mm')}</b>.
                  </Typography.Text>
                </Flex>
              ),
            },
          ]} />
        </Flex>
      )}
    </Modal>
  );
}

function LiveStatus({ used, expired, online, machineName, onNewCode, newCodeLoading }: {
  used: boolean; expired: boolean; online: boolean; machineName: string | null; onNewCode: () => void; newCodeLoading: boolean;
}) {
  if (used && online) {
    return <Alert type="success" showIcon icon={<CheckCircleFilled />} message={<>Agent đã kết nối ✓ {machineName && <b>{machineName}</b>}</>}
      description="Giờ vào mục Máy & map kênh để gán máy xét nghiệm cho agent này." />;
  }
  if (used) {
    return <Alert type="info" showIcon icon={<LoadingOutlined />} message={<>Đã ghép nối{machineName && <> với <b>{machineName}</b></>} — đang chờ agent báo về…</>} />;
  }
  if (expired) {
    return <Alert type="warning" showIcon message="Mã đã hết hạn — bấm Tạo mã mới"
      action={<Button size="small" icon={<ReloadOutlined />} loading={newCodeLoading} onClick={onNewCode}>Tạo mã mới</Button>} />;
  }
  return <Alert type="info" showIcon icon={<LoadingOutlined />} message="Đang chờ agent… hộp thoại tự cập nhật khi agent kết nối." />;
}

/** Đồng hồ mỗi giây khi hộp thoại mở — để biết mã hết hạn mà không cần hỏi máy chủ. */
function useNow(active: boolean) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!active) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1_000);
    return () => clearInterval(timer);
  }, [active]);
  return now;
}

/** So SHA-256 với header của máy chủ. Trang chạy qua http thường (không phải localhost) thì trình duyệt không có crypto.subtle → bỏ qua. */
async function checkSha256(blob: Blob, expected: string | null) {
  if (!expected || !globalThis.crypto?.subtle) return;
  const digest = await crypto.subtle.digest('SHA-256', await blob.arrayBuffer());
  const actual = Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, '0')).join('');
  if (actual.toLowerCase() !== expected.toLowerCase()) throw new Error('Tệp tải về bị hỏng (sai mã kiểm tra) — tải lại');
}
