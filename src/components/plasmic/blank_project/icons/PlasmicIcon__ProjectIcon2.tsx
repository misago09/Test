// @ts-nocheck
/* eslint-disable */
/* tslint:disable */
/* prettier-ignore-start */
import React from "react";
import { classNames } from "@plasmicapp/react-web";

export type ProjectIcon2IconProps = React.ComponentProps<"svg"> & {
  title?: string;
};

export function ProjectIcon2Icon(props: ProjectIcon2IconProps) {
  const { className, style, title, ...restProps } = props;
  return (
    <svg
      xmlns={"http://www.w3.org/2000/svg"}
      fill={"none"}
      viewBox={"0 0 28 34"}
      height={"1em"}
      width={"1em"}
      className={classNames("plasmic-default__svg", className)}
      style={style}
      {...restProps}
    >
      {title && <title>{title}</title>}

      <path
        d={
          "M0 0h1.75v33.25H0V0zm3.5 33.25V0H28v33.25H3.5zM7 4.375V7h17.5V4.375H7zm0 7V14h2.625v-2.625H7zm5.25 0V14h11.375v-2.625H12.25zm-5.25 7V21h2.625v-2.625H7zm5.25 0V21h10.5v-2.625h-10.5zm-5.25 7V28h2.625v-2.625H7zm5.25 0V28H24.5v-2.625H12.25z"
        }
        fill={"currentColor"}
      ></path>
    </svg>
  );
}

export default ProjectIcon2Icon;
/* prettier-ignore-end */
