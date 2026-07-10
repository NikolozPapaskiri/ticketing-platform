"use client";

import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useForm } from "react-hook-form";
import { z } from "zod";
import { register, ApiClientError } from "@/lib/client-api";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

const schema = z.object({
  email: z.string().email(),
  password: z.string().min(8, "Use at least 8 characters.")
});

type FormValues = z.infer<typeof schema>;

export function RegisterForm() {
  const router = useRouter();
  const search = useSearchParams();
  const queryClient = useQueryClient();
  const returnTo = search.get("returnTo") ?? "/account";
  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: { email: "", password: "" }
  });

  const mutation = useMutation({
    mutationFn: register,
    onSuccess: (result) => {
      queryClient.setQueryData(["me"], result.user);
      router.replace(returnTo);
      router.refresh();
    }
  });

  return (
    <Card className="w-full">
      <CardHeader>
        <CardTitle>Create Customer Account</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="grid gap-4" onSubmit={form.handleSubmit((values) => mutation.mutate(values))}>
          <div className="grid gap-2">
            <Label htmlFor="email">Email</Label>
            <Input id="email" type="email" autoComplete="email" {...form.register("email")} />
            {form.formState.errors.email ? <p className="text-sm text-destructive">{form.formState.errors.email.message}</p> : null}
          </div>
          <div className="grid gap-2">
            <Label htmlFor="password">Password</Label>
            <Input id="password" type="password" autoComplete="new-password" {...form.register("password")} />
            {form.formState.errors.password ? (
              <p className="text-sm text-destructive">{form.formState.errors.password.message}</p>
            ) : null}
          </div>
          {mutation.isError ? (
            <p className="rounded-[8px] border border-destructive/30 bg-red-50 px-3 py-2 text-sm text-destructive">
              {mutation.error instanceof ApiClientError ? mutation.error.message : "Registration failed."}
            </p>
          ) : null}
          <Button type="submit" disabled={mutation.isPending}>
            {mutation.isPending ? "Creating account..." : "Create account"}
          </Button>
          <p className="text-center text-sm text-muted-foreground">
            Already registered?{" "}
            <Link className="font-medium text-primary" href={`/login?returnTo=${encodeURIComponent(returnTo)}`}>
              Login
            </Link>
          </p>
        </form>
      </CardContent>
    </Card>
  );
}
