"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiClientError, createTenant, listTenants, registerStaff } from "@/lib/client-api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

function slugify(value: string) {
  return value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}

function errorText(error: unknown) {
  return error instanceof ApiClientError ? error.message : error instanceof Error ? error.message : "Request failed.";
}

export function AdminDashboard() {
  const queryClient = useQueryClient();
  const tenants = useQuery({ queryKey: ["admin-tenants"], queryFn: listTenants });
  const [tenantName, setTenantName] = useState("");
  const [tenantSlug, setTenantSlug] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("Staff123$");
  const [role, setRole] = useState<"OrganizerStaff" | "PlatformAdmin">("OrganizerStaff");
  const [tenantId, setTenantId] = useState("");

  const create = useMutation({
    mutationFn: () => createTenant({ name: tenantName, slug: tenantSlug || slugify(tenantName) }),
    onSuccess: (tenant) => {
      setTenantName("");
      setTenantSlug("");
      setTenantId(tenant.id);
      void queryClient.invalidateQueries({ queryKey: ["admin-tenants"] });
    }
  });

  const provision = useMutation({
    mutationFn: () => registerStaff({ email, password, role, tenantId: role === "OrganizerStaff" ? tenantId : null }),
    onSuccess: () => {
      setEmail("");
      setPassword("Staff123$");
    }
  });

  return (
    <div className="space-y-6">
      <div>
        <p className="text-sm font-medium uppercase tracking-[0.18em] text-primary">Platform Admin</p>
        <h1 className="mt-2 text-3xl font-semibold">Tenants and Staff Provisioning</h1>
      </div>

      <div className="grid gap-6 lg:grid-cols-[1fr_420px]">
        <Card>
          <CardHeader>
            <CardTitle>Tenants</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            {tenants.data?.map((tenant) => (
              <button
                key={tenant.id}
                type="button"
                className={`w-full rounded-[8px] border p-3 text-left ${tenantId === tenant.id ? "border-primary bg-teal-50" : "border-border bg-card"}`}
                onClick={() => setTenantId(tenant.id)}
              >
                <div className="flex items-center justify-between gap-3">
                  <span className="font-medium">{tenant.name}</span>
                  <Badge variant="secondary">{tenant.slug}</Badge>
                </div>
              </button>
            ))}
          </CardContent>
        </Card>

        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>Create Tenant</CardTitle>
            </CardHeader>
            <CardContent>
              <form
                className="grid gap-3"
                onSubmit={(event) => {
                  event.preventDefault();
                  create.mutate();
                }}
              >
                <Label htmlFor="tenant-name">Name</Label>
                <Input
                  id="tenant-name"
                  value={tenantName}
                  onChange={(event) => { setTenantName(event.target.value); setTenantSlug(slugify(event.target.value)); }}
                />
                <Label htmlFor="tenant-slug">Slug</Label>
                <Input id="tenant-slug" value={tenantSlug} onChange={(event) => setTenantSlug(slugify(event.target.value))} />
                <Button type="submit" disabled={!tenantName || create.isPending}>Create tenant</Button>
                {create.isError ? <p className="text-sm text-destructive">{errorText(create.error)}</p> : null}
              </form>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Provision User</CardTitle>
            </CardHeader>
            <CardContent>
              <form
                className="grid gap-3"
                onSubmit={(event) => {
                  event.preventDefault();
                  provision.mutate();
                }}
              >
                <Label htmlFor="staff-email">Email</Label>
                <Input id="staff-email" type="email" value={email} onChange={(event) => setEmail(event.target.value)} />
                <Label htmlFor="staff-password">Password</Label>
                <Input id="staff-password" type="password" value={password} onChange={(event) => setPassword(event.target.value)} />
                <Label htmlFor="staff-role">Role</Label>
                <select
                  id="staff-role"
                  className="h-10 rounded-[8px] border border-border bg-card px-3 text-sm"
                  value={role}
                  onChange={(event) => setRole(event.target.value as "OrganizerStaff" | "PlatformAdmin")}
                >
                  <option value="OrganizerStaff">Organizer staff</option>
                  <option value="PlatformAdmin">Platform admin</option>
                </select>
                {role === "OrganizerStaff" ? (
                  <>
                    <Label htmlFor="staff-tenant">Tenant</Label>
                    <select
                      id="staff-tenant"
                      className="h-10 rounded-[8px] border border-border bg-card px-3 text-sm"
                      value={tenantId}
                      onChange={(event) => setTenantId(event.target.value)}
                    >
                      <option value="">Select tenant</option>
                      {tenants.data?.map((tenant) => (
                        <option key={tenant.id} value={tenant.id}>{tenant.name}</option>
                      ))}
                    </select>
                  </>
                ) : null}
                <Button type="submit" disabled={!email || !password || (role === "OrganizerStaff" && !tenantId) || provision.isPending}>
                  Provision user
                </Button>
                {provision.data ? <p className="text-sm text-muted-foreground">Created {provision.data.email} as {provision.data.role}</p> : null}
                {provision.isError ? <p className="text-sm text-destructive">{errorText(provision.error)}</p> : null}
              </form>
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}
