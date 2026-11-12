-- RunWise admin access. Run once in the Supabase dashboard: SQL Editor → New query → paste → Run.
--
-- Who is an admin lives in its own table, NOT in profiles: users can edit their own profile row
-- from the app, so an is_admin column there could be switched on by anyone.

create table if not exists public.admins (
    user_id    uuid primary key references auth.users (id) on delete cascade,
    created_at timestamptz not null default now()
);

-- Row Level Security on with no policies: app users (anon / authenticated) can't read or change it.
-- Only the admin site's server, using the secret key, can.
alter table public.admins enable row level security;
revoke all on public.admins from anon, authenticated;

-- Make yourself an admin (change the email). Run this line again for each extra admin.
insert into public.admins (user_id)
select id from auth.users where email = 'your-email@example.com'
on conflict (user_id) do nothing;

-- Check who is an admin:
-- select a.user_id, u.email, a.created_at from public.admins a join auth.users u on u.id = a.user_id;
