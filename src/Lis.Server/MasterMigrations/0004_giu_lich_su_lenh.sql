-- Xoá agent không còn xoá lịch sử lệnh: bỏ khoá ngoại agent_command.agent_id (ON DELETE CASCADE) và lưu tên agent vào từng lệnh lúc xoá.
DO $$
DECLARE
    constraint_name text;
BEGIN
    FOR constraint_name IN
        SELECT c.conname
        FROM pg_constraint c
        WHERE c.conrelid = 'agent_command'::regclass
          AND c.contype = 'f'
          AND c.confrelid = 'agent'::regclass
    LOOP
        EXECUTE format('ALTER TABLE agent_command DROP CONSTRAINT %I', constraint_name);
    END LOOP;
END $$;

ALTER TABLE agent_command ADD COLUMN agent_name text NULL;
